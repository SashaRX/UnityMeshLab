"""Rank transfer defects, run failures and variants from a corpus variant-matrix capture.

The input is the schema-2 JSONL written by the Unity matrix audit (see MATRIX.md).
Rankings are diagnostic. A lower defect score is not proof of correct cross-LOD
correspondence, and the score weights are a documented heuristic, not the
production acceptance rule.
"""
import argparse
import collections
import json
import math
import re
import statistics
from pathlib import Path

from report import compare

DEFAULT_VARIANT = "default"

# Severity order and weights for the per-target defect score. Counts are raw
# per-face/per-pair counts; stretch contributes per target, not per face.
WEIGHTS = dict(invalid=1000.0, collapsed=100.0, unwritten=30.0, overlap=10.0, extreme=50.0, stretch=5.0,
               unmatched=1.0, outOfBounds=0.1)
SEVERITY = ("error", "refused", "incomplete-scan", "invalid-uv", "collapsed-uv", "unwritten-shell", "overlap",
            "extreme-stretch", "stretch>4", "unmatched", "out-of-bounds", "recovery-disabled")


def load(paths):
    """Read every JSONL capture; blank lines are ignored, other malformed lines fail."""
    rows = []
    for path in paths:
        for number, line in enumerate(Path(path).read_text(encoding="utf-8-sig").splitlines(), 1):
            if line.strip():
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError as error:
                    raise ValueError(f"{path}:{number}: {error}") from error
    return rows


def finite(value):
    return isinstance(value, (int, float)) and math.isfinite(value)


def collapsed(row):
    """Direct stored-UV collapses; falls back to the combined counter minus 3D zero-area faces."""
    direct = row.get("uvDegenerateFaces", -1)
    if isinstance(direct, int) and direct >= 0:
        return direct
    return max(0, row["quality"].get("degenerateFaces", 0) - max(0, row.get("geometryDegenerateFaces", 0)))


def defects(row):
    """Return {class: magnitude} for one measured target row."""
    q = row["quality"]
    worst = q.get("worstAnisotropy", 0.0)
    found = {}
    if not q.get("overlapScanComplete", False):
        found["incomplete-scan"] = 1
    if q.get("invalidFaces", 0) > 0:
        found["invalid-uv"] = q["invalidFaces"]
    if collapsed(row) > 0:
        found["collapsed-uv"] = collapsed(row)
    if row.get("unwrittenVertices", 0) > 0:
        found["unwritten-shell"] = row["unwrittenVertices"]
    if q.get("overlapPairs", 0) > 0:
        found["overlap"] = q["overlapPairs"]
    if not finite(worst) or worst > 10000:
        found["extreme-stretch"] = 1
    elif worst > 4:
        found["stretch>4"] = 1
    if row.get("misses", 0) > 0:
        found["unmatched"] = row["misses"]
    outside = max(row.get("rawOutOfBounds", 0), q.get("outOfBoundsVertices", 0))
    if outside > 0:
        found["out-of-bounds"] = outside
    if row.get("sourceAtlasRecoveryAllowed") == 0:
        # Diagnostic, not a UV defect: a dirty source atlas disabled Phase-3 recovery.
        found["recovery-disabled"] = 1
    return found


def score(row):
    """Weighted heuristic defect score of one measured target row (lower is better)."""
    found = defects(row)
    if "incomplete-scan" in found:
        return math.inf
    worst = row["quality"].get("worstAnisotropy", 0.0)
    value = WEIGHTS["invalid"] * found.get("invalid-uv", 0) + WEIGHTS["collapsed"] * found.get("collapsed-uv", 0)
    value += WEIGHTS["unwritten"] * found.get("unwritten-shell", 0)
    value += WEIGHTS["overlap"] * found.get("overlap", 0) + WEIGHTS["unmatched"] * found.get("unmatched", 0)
    value += WEIGHTS["outOfBounds"] * found.get("out-of-bounds", 0)
    if "extreme-stretch" in found:
        value += WEIGHTS["extreme"]
    elif "stretch>4" in found:
        value += WEIGHTS["stretch"] * min(1.0, math.log10(worst / 4) + 0.1)
    return value


def signature(row):
    """Group run failures by level, status, exception type and digit-free first message line."""
    message = (row.get("error") or "").strip().splitlines()
    head = re.sub(r"\d+(\.\d+)?", "#", message[0] if message else "")[:160]
    return row.get("level", "?"), row["status"], row.get("errorType") or "", head


def median(values):
    values = [v for v in values if finite(v)]
    return statistics.median(values) if values else None


def runs(rows):
    """Index run rows by (level, model, variant), keeping every repetition."""
    index = collections.defaultdict(list)
    for row in rows:
        if row.get("status") in ("run", "error", "refused") and row.get("variant"):
            index[(row.get("level"), row.get("model"), row["variant"])].append(row)
    return index


def targets(rows):
    """Index measured rows by (level, model, variant) → {target key: first repetition}.

    The key is the mesh pair and whether the row measures the source or the target. Method
    and profile stay out of it: they are what a variant changes, so a key holding them would
    never meet the default's."""
    index = collections.defaultdict(dict)
    for row in rows:
        if row.get("status") != "measured" or row.get("schema", 1) < 2:
            continue
        role = "source" if row.get("method") == "source" else "target"
        key = (row.get("source", ""), row.get("target", ""), role)
        index[(row["level"], row["model"], row["variant"])].setdefault(key, row)
    return index


def failed(run_rows):
    return any(r["status"] in ("error", "refused") for r in run_rows)


def succeeded(run_rows):
    return any(r["status"] == "run" for r in run_rows) and not failed(run_rows)


# Schema-2 counters that enter the score but not report.compare; lower is better, -1 is unknown.
# Only grouped methods count misses (the others write 0), so misses compare within one method.
COUNTERS = ("unwrittenVertices", "misses", "rawOutOfBounds")


def compare_rows(candidate, baseline):
    """report.compare, with the schema-2 counters folded into the same dominance rule."""
    result = compare(candidate, baseline)
    if result in ("incomplete", "invalid"):
        return result
    better, worse = result in ("better", "tradeoff"), result in ("worse", "tradeoff")
    for key in COUNTERS:
        if key == "misses" and candidate.get("method") != baseline.get("method"):
            continue
        value, original = candidate.get(key, -1), baseline.get(key, -1)
        if value >= 0 and original >= 0:
            better |= value < original
            worse |= value > original
    return "tradeoff" if better and worse else "better" if better else "worse" if worse else "same"


def rank_failures(rows):
    groups = collections.defaultdict(list)
    for row in rows:
        if row.get("status") in ("error", "refused"):
            groups[signature(row)].append(row)
    table = [dict(level=key[0], status=key[1], errorType=key[2], message=key[3], rows=len(items),
                  models=len({r.get("model") for r in items}), variants=sorted({r.get("variant", "") for r in items}),
                  examples=sorted({r.get("model", "") for r in items})[:5])
             for key, items in groups.items()]
    order = {name: i for i, name in enumerate(SEVERITY)}
    return sorted(table, key=lambda r: (order.get(r["status"], 99), -r["models"], -r["rows"]))


def rank_defects(rows, variant=DEFAULT_VARIANT):
    """Defect classes of the chosen variant, ranked by severity, then affected models."""
    by_class = collections.defaultdict(lambda: dict(models=set(), rows=0, magnitude=0, worst=(0, "")))
    for (level, model, name), items in targets(rows).items():
        if name != variant:
            continue
        for row in items.values():
            for kind, amount in defects(row).items():
                entry = by_class[(level, kind)]
                entry["models"].add(model); entry["rows"] += 1; entry["magnitude"] += amount
                if amount > entry["worst"][0]:
                    entry["worst"] = (amount, f"{model}:{row.get('target', '')}")
    for (level, model, name), items in runs(rows).items():
        if name == variant and failed(items):
            entry = by_class[(level, "refused" if any(r["status"] == "refused" for r in items) else "error")]
            entry["models"].add(model); entry["rows"] += 1; entry["magnitude"] += 1
    order = {name: i for i, name in enumerate(SEVERITY)}
    table = [dict(level=level, defect=kind, models=len(e["models"]), targets=e["rows"], magnitude=e["magnitude"],
                  worst=e["worst"][1], worstMagnitude=e["worst"][0]) for (level, kind), e in by_class.items()]
    return sorted(table, key=lambda r: (r["level"], order.get(r["defect"], 99), -r["models"], -r["magnitude"]))


def run_time(run_rows):
    return median([r.get("milliseconds") for r in run_rows if r["status"] == "run"])


def rank_variants(rows):
    """Compare every variant with the same level's default on identical model/target keys."""
    measured, executed = targets(rows), runs(rows)
    levels = collections.defaultdict(set)
    for level, _, name in list(measured) + list(executed):
        levels[level].add(name)
    result = {}
    for level, names in sorted(levels.items()):
        models = sorted({m for (l, m, _) in list(measured) + list(executed) if l == level})
        table = []
        for name in sorted(names):
            counts = collections.Counter()
            totals, times, ratios = [], [], []
            for model in models:
                base, cand = measured.get((level, model, DEFAULT_VARIANT), {}), measured.get((level, model, name), {})
                base_runs, cand_runs = executed.get((level, model, DEFAULT_VARIANT), []), executed.get((level, model, name), [])
                if failed(cand_runs) and not failed(base_runs):
                    counts["failed"] += 1
                elif failed(base_runs) and not failed(cand_runs) and cand:
                    counts["recovered"] += 1
                # A variant that measured other targets than the default lost or added output:
                # its shared targets still compare, but its score sum is not comparable.
                covered = not base or set(cand) == set(base)
                if name != DEFAULT_VARIANT:
                    if not covered and not failed(cand_runs):
                        counts["uncovered"] += 1
                    for key in sorted(set(base) & set(cand)):
                        counts[compare_rows(cand[key], base[key])] += 1
                if cand and covered and not failed(cand_runs):
                    totals.append(sum(score(r) for r in cand.values()))
                elapsed, base_elapsed = run_time(cand_runs), run_time(base_runs)
                if elapsed is not None:
                    times.append(elapsed)
                    if base_elapsed:
                        ratios.append(elapsed / base_elapsed)
            table.append(dict(variant=name, models=len(totals), better=counts["better"], worse=counts["worse"],
                              tradeoff=counts["tradeoff"], same=counts["same"], incomplete=counts["incomplete"],
                              invalid=counts["invalid"], failed=counts["failed"], recovered=counts["recovered"],
                              uncovered=counts["uncovered"],
                              scoreTotal=sum(t for t in totals if finite(t)), scoreMedian=median(totals),
                              infiniteScores=sum(not finite(t) for t in totals),
                              msMedian=median(times), timeRatioMedian=median(ratios)))
        result[level] = sorted(table, key=lambda r: (r["variant"] != DEFAULT_VARIANT, r["worse"] + r["failed"] - r["better"],
                                                     r["scoreTotal"]))
    return result


def best_variants(rows):
    """Per level and model: lowest total score among successful variants; ties go to the faster run.

    A candidate needs a successful run, a finite score and every target any successful variant
    of that model measured: a variant that lost a target would otherwise win on a shorter sum."""
    measured, executed = targets(rows), runs(rows)
    eligible = collections.defaultdict(dict)
    for (level, model, name), items in measured.items():
        if succeeded(executed.get((level, model, name), [])):
            eligible[(level, model)][name] = items
    choices = collections.defaultdict(dict)
    for (level, model), variants in eligible.items():
        required = set().union(*variants.values())
        for name, items in variants.items():
            total = sum(score(r) for r in items.values())
            if set(items) != required or not finite(total):
                continue
            elapsed = run_time(executed.get((level, model, name), []))
            current = choices[level].get(model)
            candidate = (total, elapsed if elapsed is not None else math.inf, name)
            if current is None or candidate < current:
                choices[level][model] = candidate
    wins = {level: collections.Counter(choice[2] for choice in models.values()) for level, models in choices.items()}
    return {level: dict(sorted(counter.items(), key=lambda item: -item[1])) for level, counter in wins.items()}, \
        {level: {model: dict(variant=c[2], score=c[0], ms=c[1]) for model, c in sorted(models.items())}
         for level, models in choices.items()}


def rank_stages(rows, variant=DEFAULT_VARIANT):
    """Median per-model stage time of one variant; stages ranked by their summed medians.

    A stage recorded several times in one run (once per LOD, or inside a loop) is summed
    for that run first: the median runs over repetitions, not over occurrences."""
    samples = collections.defaultdict(lambda: collections.defaultdict(list))
    for row in rows:
        if row.get("status") != "run" or row.get("variant") != variant:
            continue
        per_run = collections.defaultdict(float)
        for stage in row.get("stages") or []:
            per_run[stage.get("stage")] += stage.get("milliseconds", 0.0)
        for name, milliseconds in per_run.items():
            samples[(row.get("level"), name)][row.get("model")].append(milliseconds)
    table = []
    for (level, stage), models in samples.items():
        medians = {model: median(values) or 0.0 for model, values in models.items()}
        slowest = max(medians, key=medians.get)
        table.append(dict(level=level, stage=stage, models=len(medians), msTotal=sum(medians.values()),
                          msMax=medians[slowest], slowestModel=slowest))
    return sorted(table, key=lambda r: -r["msTotal"])


def markdown(report):
    """Render the main tables; the JSON report stays the complete record."""
    def table(rows, columns):
        if not rows:
            return "_none_\n"
        text = "| " + " | ".join(columns) + " |\n|" + "---|" * len(columns) + "\n"
        for row in rows:
            cells = []
            for column in columns:
                value = row.get(column)
                cells.append(f"{value:.4g}" if isinstance(value, float) else ", ".join(value) if isinstance(value, list) else str(value))
            text += "| " + " | ".join(cells) + " |\n"
        return text
    out = ["# Transfer variant matrix ranking\n", "Diagnostic only: scores do not certify correspondence.\n",
           "## Run failures\n", table(report["failures"], ["level", "status", "errorType", "message", "models", "rows", "examples"]),
           f"## Defects of the `{DEFAULT_VARIANT}` variant\n",
           table(report["defects"], ["level", "defect", "models", "targets", "magnitude", "worst"])]
    for level, rows in report["variants"].items():
        out += [f"## Variants: {level}\n", table(rows, ["variant", "models", "better", "worse", "tradeoff", "same",
                                                       "failed", "recovered", "uncovered", "scoreTotal", "msMedian",
                                                       "timeRatioMedian"])]
    out += ["## Best variant wins per level\n"]
    for level, wins in report["bestWins"].items():
        out.append(f"- **{level}**: " + ", ".join(f"{name} {count}" for name, count in wins.items()) + "\n")
    out += ["\n## Slowest stages (default variant)\n", table(report["stages"][:25], ["level", "stage", "models", "msTotal", "msMax", "slowestModel"])]
    return "\n".join(out)


def check_inputs(rows):
    """Refuse to combine rows measured on different bytes of one model, as report.summarize does."""
    hashes = collections.defaultdict(set)
    for row in rows:
        if row.get("model") and row.get("inputHash"):
            hashes[row["model"]].add(row["inputHash"])
    mixed = sorted(model for model, values in hashes.items() if len(values) > 1)
    if mixed:
        raise ValueError("Captures measure different FBX bytes for: " + ", ".join(mixed) +
                         ". Rank one corpus revision at a time.")


def build(captures):
    """One row list per input capture; the report is complete only when every capture ends complete."""
    rows = [row for capture in captures for row in capture]
    check_inputs(rows)
    complete = all(capture and capture[-1].get("status") == "complete" for capture in captures)
    wins, best = best_variants(rows)
    return dict(terminalMarker=complete, rows=len(rows),
                models=len({r.get("model") for r in rows if r.get("model")}),
                failures=rank_failures(rows), defects=rank_defects(rows), variants=rank_variants(rows),
                bestWins=wins, best=best, stages=rank_stages(rows))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", type=Path, nargs="+", help="Schema-2 JSONL captures (several runs may be combined).")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    report = build([load([path]) for path in args.captures])
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "ranking.json").write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")
    (args.out / "ranking.md").write_text(markdown(report), encoding="utf-8")
    print(json.dumps(dict(rows=report["rows"], models=report["models"], terminalMarker=report["terminalMarker"],
                          failures=len(report["failures"]), bestWins=report["bestWins"]), indent=2))
    if not report["terminalMarker"]:
        parser.exit(2, "Capture has no terminal 'complete' row; rankings cover a partial run.\n")


if __name__ == "__main__":
    main()
