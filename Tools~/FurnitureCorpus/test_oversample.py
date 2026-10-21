import unittest

from oversample import AT_RESOLUTION, analyze, render
from report import METRICS


def row(profile="default", method="grouped", target="chair_LOD1", side=2048, **quality):
    """A clean measurement of one fixed pair; keyword arguments override quality metrics."""
    return dict(status="measured", model="chair", source="chair_LOD0", target=target,
                profile=profile, method=method, inputHash="same", resolution=512, atlasSide=side,
                geometryDegenerateFaces=0, uvDegenerateFaces=0,
                quality=dict.fromkeys(METRICS, 0) | {"overlapScanComplete": True} | quality)


class OversampleTests(unittest.TestCase):
    def capture(self):
        """Source improves; internal-side transfer trades overlaps for collapse; the
        requested-resolution transfer keeps the source gain."""
        return [
            row(method="source", overlapPairs=4),
            row("oversample-8", "source", side=4096, overlapPairs=1),
            row(overlapPairs=6),
            row("oversample-8", "grouped", side=4096, overlapPairs=2, degenerateFaces=3),
            row(method=AT_RESOLUTION, side=512, overlapPairs=5),
            row("oversample-8", AT_RESOLUTION, side=512, overlapPairs=2),
        ]

    def test_separates_the_pack_from_the_transfer_side(self):
        analysis = analyze(self.capture())
        groups = analysis["comparisons"]
        self.assertEqual(analysis["atlasSideControlPairs"], 1)
        self.assertEqual(groups["packed source: oversample 8 vs 4"]["verdicts"], {"better": 1})
        internal = groups["transfer, internal atlas side: oversample 8 vs 4"]
        self.assertEqual(internal["verdicts"], {"tradeoff": 1})
        self.assertEqual(internal["metrics"]["overlapPairs"], {"better": 1})
        self.assertEqual(internal["metrics"]["degenerateFaces"], {"worse": 1})
        self.assertEqual(internal["tradeoffs"], {"better overlapPairs | worse degenerateFaces": 1})
        self.assertEqual(groups["transfer, requested resolution: oversample 8 vs 4"]["verdicts"], {"better": 1})
        self.assertEqual(groups["transfer side only, oversample 4: requested vs internal"]["verdicts"], {"better": 1})
        self.assertEqual(groups["transfer side only, oversample 8: requested vs internal"]["verdicts"], {"better": 1})

    def test_retention_pairs_the_source_verdict_with_each_transfer(self):
        retention = analyze(self.capture())["retention"]
        self.assertEqual(retention["grouped"], {"source better -> transfer tradeoff": 1})
        self.assertEqual(retention[AT_RESOLUTION], {"source better -> transfer better": 1})

    def test_reached_oversample_counts_each_source_mesh_once(self):
        rows = self.capture() + [row(method="source", target="chair_LOD2", overlapPairs=4),
                                 row("oversample-8", "source", target="chair_LOD2", side=2048, overlapPairs=1)]
        reached = analyze(rows)["oversampleReached"]
        self.assertEqual(reached, {"default": {"4": 1}, "oversample-8": {"8": 1}},
                         "Source rows repeat per target; the first row of each source mesh counts.")

    def test_capture_without_the_control_reports_missing_rows(self):
        rows = [r for r in self.capture() if r["method"] != AT_RESOLUTION]
        analysis = analyze(rows)
        self.assertEqual(analysis["atlasSideControlPairs"], 0)
        self.assertEqual(analysis["comparisons"]["transfer, requested resolution: oversample 8 vs 4"]["verdicts"], {"missing": 1})
        self.assertIn("MESHLAB_CORPUS_OVERSAMPLE_ANALYSIS=1", render(analysis))

    def test_partial_control_is_reported_as_partial(self):
        other = [dict(r, target="chair_LOD2") for r in self.capture() if r["method"] != AT_RESOLUTION]
        analysis = analyze(self.capture() + other)
        self.assertEqual((analysis["pairs"], analysis["atlasSideControlPairs"]), (2, 1))
        self.assertIn("Only 1 of 2 pairs", render(analysis))

    def test_recovery_gate_pairs_the_traced_gate_with_the_transfer_verdict(self):
        rows = self.capture()
        rows[2] |= dict(traced=True, sourceAtlasRecoveryAllowed=False, recoveredShells=0)
        rows[3] |= dict(traced=True, sourceAtlasRecoveryAllowed=True, recoveredShells=2)
        analysis = analyze(rows)
        self.assertEqual(analysis["recoveryGate"]["grouped"],
                         {"gate closed->open, recovered shells more -> transfer tradeoff": 1})
        self.assertEqual(analysis["recoveryGate"][AT_RESOLUTION], {}, "Untraced rows are left out.")
        self.assertIn("Source-atlas recovery gate and `grouped`", render(analysis))

    def test_duplicate_or_mismatched_rows_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            analyze(self.capture() + [row()])
        with self.assertRaisesRegex(ValueError, "Mismatched"):
            analyze(self.capture() + [dict(row(method="uv0-nearest"), inputHash="other")])

    def test_incomplete_scans_are_counted_but_not_broken_down(self):
        rows = self.capture()
        rows[3]["quality"]["overlapScanComplete"] = False
        group = analyze(rows)["comparisons"]["transfer, internal atlas side: oversample 8 vs 4"]
        self.assertEqual(group["verdicts"], {"incomplete": 1})
        self.assertEqual(group["metrics"], {})

    def test_render_contains_no_model_names(self):
        text = render(analyze(self.capture()))
        self.assertNotIn("chair", text)
        self.assertIn("| packed source: oversample 8 vs 4 | 1 | 0 | 0 | 0 | 0 |", text)


if __name__ == "__main__":
    unittest.main()
