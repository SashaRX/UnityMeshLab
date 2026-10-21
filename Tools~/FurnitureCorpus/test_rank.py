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


if __name__ == "__main__":
    unittest.main()
