import copy
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from report import METRICS, compare, compare_runs, main, summarize


def row(profile="default", method="grouped"):
    """Create a complete, clean measurement for one fixed input pair."""
    return dict(status="measured", model="chair", source="chair_LOD0", target="chair_LOD1",
                profile=profile, method=method, inputHash="same", resolution=512, schema=1,
                atlasSide=512, milliseconds=0, rawOutOfBounds=0, misses=0, fallbackVertices=0, uvTriangleAreaSum=0,
                geometryDegenerateFaces=0, uvDegenerateFaces=0,
                quality=dict.fromkeys(METRICS, 0) | {"overlapScanComplete": True, "faces": 1})


class ReportTests(unittest.TestCase):
    def complete_capture(self):
        """Create the complete nine-variant inventory used by cross-run controls."""
        rows = [row(method=m) for m in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
        rows += [row(p, m) for p in ("no-reparameterization", "oversample-8") for m in ("source", "grouped")]
        return rows + [dict(status="complete", fileCount=1, pairCount=1, errorCount=0)]

    def test_compare_runs_records_per_pair_tradeoffs(self):
        """An aggregate improvement must not hide one target's added UV collapse."""
        baseline = self.complete_capture()
        candidate = copy.deepcopy(baseline)
        baseline[1]["quality"]["overlapPairs"] = 10
        candidate[1]["uvDegenerateFaces"] = 1
        report = compare_runs(candidate, baseline)
        self.assertEqual(report["measuredRows"], 9)
        self.assertEqual(report["comparisons"]["default/grouped"], {"tradeoff": 1})
        self.assertEqual(len(report["changed"]), 1)
        self.assertEqual(report["changed"][0]["target"], "chair_LOD1")

    def test_compare_runs_requires_matching_inputs(self):
        """Changed source bytes, resolution and model inventories cannot be paired."""
        baseline = self.complete_capture()
        for field, value in (("inputHash", "changed"), ("resolution", 1024), ("schema", 2), ("model", "different")):
            candidate = copy.deepcopy(baseline)
            for measurement in candidate[:-1]:
                measurement[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                compare_runs(candidate, baseline)
        baseline.insert(-1, dict(status="excluded", model="no-lods-a"))
        baseline[-1]["fileCount"] = 2
        candidate = copy.deepcopy(baseline)
        candidate[-2]["model"] = "no-lods-b"
        with self.assertRaises(ValueError):
            compare_runs(candidate, baseline)

    def test_compare_runs_rejects_incomplete_capture_or_scan(self):
        """Neither interrupted runs nor unfinished overlap scans establish a regression comparison."""
        baseline = self.complete_capture()
        incomplete_scan = copy.deepcopy(baseline)
        incomplete_scan[0]["quality"]["overlapScanComplete"] = False
        for candidate in (baseline[:-1], incomplete_scan):
            with self.assertRaises(ValueError):
                compare_runs(candidate, baseline)
            with self.assertRaises(ValueError):
                compare_runs(baseline, candidate)

    def test_cli_gate_preserves_report_and_fails_on_regression(self):
        """The executable gate succeeds on identical inputs and retains evidence when a regression fails it."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = self.complete_capture()
            (root / "before.jsonl").write_text("\n".join(map(json.dumps, baseline)), encoding="utf-8")
            for regress in (False, True):
                candidate = copy.deepcopy(baseline)
                candidate[1]["uvDegenerateFaces"] = int(regress)
                (root / "after.jsonl").write_text("\n".join(map(json.dumps, candidate)), encoding="utf-8")
                args = [str(root / "after.jsonl"), "--baseline", str(root / "before.jsonl"),
                        "--out", str(root / str(regress)), "--require-no-regressions"]
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    if regress:
                        with self.assertRaises(SystemExit) as failure:
                            main(args)
                        self.assertEqual(failure.exception.code, 1)
                    else:
                        main(args)
                report = json.loads((root / str(regress) / "summary.json").read_text(encoding="utf-8"))
                self.assertEqual(report["baselineComparison"]["comparisons"]["default/grouped"],
                                 {"worse" if regress else "same": 1})

    def test_cli_gate_requires_a_baseline(self):
        """A gate with no reference capture must fail before reading input files."""
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as failure:
            main(["unused.jsonl", "--out", "unused-report", "--require-no-regressions"])
        self.assertEqual(failure.exception.code, 2)

    def test_overlap_improvement_cannot_hide_collapse(self):
        """Improving one metric while adding a defect must remain a tradeoff."""
        baseline, candidate = row(), row()
        baseline["quality"]["overlapPairs"] = 10
        candidate["quality"]["degenerateFaces"] = 1
        self.assertEqual(compare(candidate, baseline), "tradeoff")

    def test_incomplete_scan_cannot_win(self):
        """An unfinished intersection scan cannot establish an improvement."""
        baseline, candidate = row(), row()
        baseline["quality"]["overlapPairs"] = 10
        candidate["quality"]["overlapScanComplete"] = False
        self.assertEqual(compare(candidate, baseline), "incomplete")

    def test_direct_uv_collapse_count_overrides_rounded_jacobian_metric(self):
        """A singular stored UV triangle must count even if the Jacobian looks finite."""
        baseline, candidate = row(), row()
        candidate["uvDegenerateFaces"] = 1
        self.assertEqual(compare(candidate, baseline), "worse")

    def test_missing_variants_and_missing_terminal_marker(self):
        """Partial variant sets and interrupted captures must not look complete."""
        self.assertFalse(summarize([row()])["complete"])
        report = summarize([row(), {"status": "complete"}])
        self.assertFalse(report["complete"])
        self.assertEqual(len(report["missing"]), 8)

    def test_duplicate_and_mismatched_inputs_rejected(self):
        """Duplicate rows and changed source bytes invalidate paired comparisons."""
        with self.assertRaises(ValueError):
            summarize([row(), row()])
        changed = row(method="surface-normal")
        changed["inputHash"] = "different"
        with self.assertRaises(ValueError):
            summarize([row(), changed])

    def test_complete_run_with_explicit_exclusion(self):
        """Recorded exclusions are allowed while processing errors prevent completion."""
        rows = [row(method=m) for m in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
        rows += [row(p, m) for p in ("no-reparameterization", "oversample-8") for m in ("source", "grouped")]
        rows += [dict(status="excluded", model="no-lods"), dict(status="complete", fileCount=2, pairCount=1, errorCount=0)]
        self.assertTrue(summarize(rows)["complete"])
        failed = copy.deepcopy(rows)
        failed.insert(-1, dict(status="error", model="broken"))
        self.assertFalse(summarize(failed)["complete"])

    def test_missing_whole_pair_or_entire_file_is_not_complete(self):
        """Terminal inventory detects missing groups even if all surviving rows exist."""
        rows = [row(method=m) for m in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
        rows += [row(p, m) for p in ("no-reparameterization", "oversample-8") for m in ("source", "grouped")]
        for files, pairs in ((2, 1), (1, 2)):
            self.assertFalse(summarize(rows + [dict(status="complete", fileCount=files, pairCount=pairs, errorCount=0)])["complete"])


if __name__ == "__main__":
    unittest.main()
