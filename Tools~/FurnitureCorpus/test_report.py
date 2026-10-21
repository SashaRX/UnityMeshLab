import copy
import unittest

from report import METRICS, compare, summarize


def row(profile="default", method="grouped"):
    """Create a complete, clean measurement for one fixed input pair."""
    return dict(status="measured", model="chair", source="chair_LOD0", target="chair_LOD1",
                profile=profile, method=method, inputHash="same",
                geometryDegenerateFaces=0, uvDegenerateFaces=0,
                quality=dict.fromkeys(METRICS, 0) | {"overlapScanComplete": True})


class ReportTests(unittest.TestCase):
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

    def test_profiles_compare_with_the_default_row_of_the_same_method(self):
        """An extra transfer method is compared with the default profile's row of that method."""
        rows = [row(method=m) for m in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
        rows += [row(p, m) for p in ("no-reparameterization", "oversample-8") for m in ("source", "grouped")]
        control, candidate = row(method="grouped-at-resolution"), row("oversample-8", "grouped-at-resolution")
        control["quality"]["overlapPairs"] = 3
        candidate["quality"]["overlapPairs"] = 2
        comparisons = summarize(rows + [control, candidate])["comparisons"]
        self.assertEqual(comparisons["oversample-8/grouped-at-resolution"], {"better": 1})
        self.assertEqual(comparisons["default/grouped-at-resolution"], {"worse": 1},
                         "Another default-profile method is compared with default grouped transfer.")
        self.assertEqual(comparisons["oversample-8/grouped"], {"same": 1})

    def test_missing_whole_pair_or_entire_file_is_not_complete(self):
        """Terminal inventory detects missing groups even if all surviving rows exist."""
        rows = [row(method=m) for m in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
        rows += [row(p, m) for p in ("no-reparameterization", "oversample-8") for m in ("source", "grouped")]
        for files, pairs in ((2, 1), (1, 2)):
            self.assertFalse(summarize(rows + [dict(status="complete", fileCount=files, pairCount=pairs, errorCount=0)])["complete"])


if __name__ == "__main__":
    unittest.main()
