import json
import math
import tempfile
import unittest
from pathlib import Path

import rank
from report import METRICS


def measured(model="chair", variant="default", level="forward", target="chair_LOD1", **quality):
    """One clean schema-2 target row; keyword arguments override quality metrics."""
    values = dict.fromkeys(METRICS, 0) | {"overlapScanComplete": True, "worstAnisotropy": 1.0} | quality
    return dict(schema=2, status="measured", level=level, model=model, variant=variant, source="chair_LOD0",
                target=target, method="", profile="", inputHash="h", geometryDegenerateFaces=0,
                uvDegenerateFaces=0, rawOutOfBounds=0, misses=0, quality=values)


def run(model="chair", variant="default", level="forward", ms=100.0, status="run", error="", stages=None):
    return dict(schema=2, status=status, level=level, model=model, variant=variant, milliseconds=ms,
                error=error, errorType="InvalidOperationException" if error else "", stages=stages or [])


class RankTests(unittest.TestCase):
    def test_defect_classes_keep_collapse_separate_from_geometry(self):
        """3D zero-area faces must not be reported as collapsed UVs when the direct counter is present."""
        row = measured(degenerateFaces=3, overlapPairs=2, worstAnisotropy=6.0)
        row["geometryDegenerateFaces"] = 3
        found = rank.defects(row)
        self.assertNotIn("collapsed-uv", found)
        self.assertEqual(found["overlap"], 2)
        self.assertIn("stretch>4", found)

    def test_unwritten_shell_is_a_defect_but_disabled_recovery_scores_nothing(self):
        """Vertices left at the default UV2 rank as a defect; the source gate state is diagnostic only."""
        row = measured()
        row["unwrittenVertices"], row["sourceAtlasRecoveryAllowed"] = 12, 0
        found = rank.defects(row)
        self.assertEqual(found["unwritten-shell"], 12)
        self.assertIn("recovery-disabled", found)
        self.assertEqual(rank.score(row), rank.WEIGHTS["unwritten"] * 12)

    def test_incomplete_scan_scores_infinite(self):
        """An unfinished intersection scan can never rank as the best variant."""
        self.assertTrue(math.isinf(rank.score(measured(overlapScanComplete=False))))

    def test_failure_signatures_merge_numeric_variants(self):
        """Messages differing only by face indices are one error class across models."""
        rows = [run("a", status="refused", error="Face 12 collapses in the shared frame"),
                run("b", status="refused", error="Face 9057 collapses in the shared frame")]
        failures = rank.rank_failures(rows)
        self.assertEqual(len(failures), 1)
        self.assertEqual(failures[0]["models"], 2)

    def test_variant_that_fails_where_default_succeeds_is_counted(self):
        rows = [run(), measured(), run(variant="cuts=on", status="error", error="boom")]
        table = {r["variant"]: r for r in rank.rank_variants(rows)["forward"]}
        self.assertEqual(table["cuts=on"]["failed"], 1)

    def test_best_variant_prefers_fewer_defects_then_time(self):
        rows = [run(ms=100), measured(overlapPairs=5),
                run(variant="symmetry=adaptive", ms=300), measured(variant="symmetry=adaptive"),
                run(variant="symmetry=off", ms=200), measured(variant="symmetry=off")]
        wins, best = rank.best_variants(rows)
        self.assertEqual(best["forward"]["chair"]["variant"], "symmetry=off")
        self.assertEqual(wins["forward"], {"symmetry=off": 1})

    def test_tradeoff_is_not_reported_as_improvement(self):
        rows = [run(), measured(overlapPairs=10), run(variant="v"), measured(variant="v", degenerateFaces=1)]
        rows[-1]["uvDegenerateFaces"] = 1
        table = {r["variant"]: r for r in rank.rank_variants(rows)["forward"]}
        self.assertEqual((table["v"]["better"], table["v"]["tradeoff"]), (0, 1))

    def test_variant_changing_method_or_profile_meets_the_default_pair(self):
        """A method or profile variant compares on the same pair; its source row stays apart from the target row."""
        def row(variant, method, profile, **quality):
            item = measured(variant=variant, level="grouped", **quality)
            item["method"], item["profile"] = method, profile
            return item
        rows = [run(level="grouped"), row("default", "source", "default"), row("default", "grouped", "default", overlapPairs=4),
                run(variant="method=uv0-nearest", level="grouped"), row("method=uv0-nearest", "source", "default"),
                row("method=uv0-nearest", "uv0-nearest", "default", overlapPairs=1),
                run(variant="arap=off", level="grouped"), row("arap=off", "source", "no-reparameterization"),
                row("arap=off", "grouped", "no-reparameterization", overlapPairs=9)]
        table = {r["variant"]: r for r in rank.rank_variants(rows)["grouped"]}
        self.assertEqual((table["method=uv0-nearest"]["better"], table["method=uv0-nearest"]["same"]), (1, 1))
        self.assertEqual((table["arap=off"]["worse"], table["arap=off"]["same"]), (1, 1))

    def test_counter_only_change_is_not_reported_same(self):
        """More vertices left unwritten is worse even when every quality metric matches; -1 stays unknown."""
        rows = [run(), measured(), run(variant="cuts=on"), measured(variant="cuts=on")]
        rows[1]["unwrittenVertices"], rows[3]["unwrittenVertices"] = 0, 7
        rows[1]["rawOutOfBounds"], rows[3]["rawOutOfBounds"] = -1, 50
        table = {r["variant"]: r for r in rank.rank_variants(rows)["forward"]}
        self.assertEqual((table["cuts=on"]["worse"], table["cuts=on"]["same"]), (1, 0))
        grouped, nearest = measured(), measured()
        grouped["method"], grouped["misses"], nearest["method"] = "grouped", 5, "uv0-nearest"
        self.assertEqual(rank.compare_rows(nearest, grouped), "same")

    def test_best_variant_needs_a_run_a_finite_score_and_every_target(self):
        """No winner without a successful run or with an unfinished scan; a lost target cannot win on a shorter sum."""
        self.assertEqual(rank.best_variants([measured()])[1], {})
        self.assertEqual(rank.best_variants([run(), measured(overlapScanComplete=False)])[1], {})
        rows = [run(), measured(overlapPairs=1), measured(target="chair_LOD2", overlapPairs=1),
                run(variant="lost"), measured(variant="lost", overlapPairs=1)]
        self.assertEqual(rank.best_variants(rows)[1]["forward"]["chair"]["variant"], "default")

    def test_stage_ranking_uses_median_repetition(self):
        rows = [run(stages=[{"stage": "pack", "milliseconds": v}]) for v in (10.0, 30.0, 1000.0)]
        self.assertEqual(rank.rank_stages(rows)[0]["msTotal"], 30.0)

    def test_cli_reports_partial_capture(self):
        with tempfile.TemporaryDirectory() as directory:
            capture = Path(directory) / "capture.jsonl"
            capture.write_text("\n".join(json.dumps(r) for r in [run(), measured()]), encoding="utf-8")
            with self.assertRaises(SystemExit) as exit_code:
                rank.main([str(capture), "--out", str(Path(directory) / "out")])
            self.assertEqual(exit_code.exception.code, 2)
            self.assertTrue((Path(directory) / "out" / "ranking.md").exists())

    def test_cli_complete_capture_does_not_hide_a_partial_one(self):
        with tempfile.TemporaryDirectory() as directory:
            finished, partial = Path(directory) / "finished.jsonl", Path(directory) / "partial.jsonl"
            finished.write_text("\n".join(json.dumps(r) for r in [run(), measured(), dict(status="complete")]), encoding="utf-8")
            partial.write_text(json.dumps(run(model="table")), encoding="utf-8")
            with self.assertRaises(SystemExit) as exit_code:
                rank.main([str(finished), str(partial), "--out", str(Path(directory) / "out")])
            self.assertEqual(exit_code.exception.code, 2)
            self.assertIsNone(rank.main([str(finished), "--out", str(Path(directory) / "alone")]))


if __name__ == "__main__":
    unittest.main()
