import copy
import unittest

from report import METRICS, compare, summarize


def row(profile="default", method="grouped"):
    return dict(status="measured", model="chair", source="chair_LOD0", target="chair_LOD1",
                profile=profile, method=method, inputHash="same",
                quality=dict.fromkeys(METRICS, 0) | {"overlapScanComplete": True})


class ReportTests(unittest.TestCase):
    def test_overlap_improvement_cannot_hide_collapse(self):
        baseline, candidate = row(), row()
        baseline["quality"]["overlapPairs"] = 10
        candidate["quality"]["degenerateFaces"] = 1
        self.assertEqual(compare(candidate, baseline), "tradeoff")

    def test_incomplete_scan_cannot_win(self):
        baseline, candidate = row(), row()
        baseline["quality"]["overlapPairs"] = 10
        candidate["quality"]["overlapScanComplete"] = False
        self.assertEqual(compare(candidate, baseline), "incomplete")

    def test_missing_variants_and_missing_terminal_marker(self):
        self.assertFalse(summarize([row()])["complete"])
        report = summarize([row(), {"status": "complete"}])
        self.assertFalse(report["complete"])
        self.assertEqual(len(report["missing"]), 8)

    def test_duplicate_and_mismatched_inputs_rejected(self):
        with self.assertRaises(ValueError):
            summarize([row(), row()])
        changed = row(method="surface-normal")
        changed["inputHash"] = "different"
        with self.assertRaises(ValueError):
            summarize([row(), changed])

    def test_complete_run_with_explicit_exclusion(self):
        rows = [row(method=m) for m in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
        rows += [row(p, m) for p in ("no-reparameterization", "oversample-8") for m in ("source", "grouped")]
        rows += [dict(status="excluded", model="no-lods"), dict(status="complete", fileCount=2, pairCount=1, errorCount=0)]
        self.assertTrue(summarize(rows)["complete"])
        failed = copy.deepcopy(rows)
        failed.insert(-1, dict(status="error", model="broken"))
        self.assertFalse(summarize(failed)["complete"])

    def test_missing_whole_pair_or_entire_file_is_not_complete(self):
        rows = [row(method=m) for m in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
        rows += [row(p, m) for p in ("no-reparameterization", "oversample-8") for m in ("source", "grouped")]
        for files, pairs in ((2, 1), (1, 2)):
            self.assertFalse(summarize(rows + [dict(status="complete", fileCount=files, pairCount=pairs, errorCount=0)])["complete"])


if __name__ == "__main__":
    unittest.main()
