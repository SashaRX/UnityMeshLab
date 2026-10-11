# Main Quality Gate and Unity coverage

The main Sonar project enforces its server Quality Gate. PR scans use disposable
projects and report some rules as advisory; an advisory PR finding can still fail
the main server gate. Neither the gate nor the advisory policy is changed by the
coverage integration.

The failure following PR #231 had two independent causes: `new_coverage = 0.0`
against an 80% minimum, and 60 new issues against a zero-issue limit (37 S3776,
15 S3358, 8 S3267). Unity tests had run locally, but their coverage was never
imported into the licence-free scanner build. The read-only backlog workflow also
selected an invalid API token before the valid scanner token; it now passes both
tokens separately so the reporting tool can try its existing fallback.

## Main coverage flow

1. The plan job records the exact main commit. A feature-branch dispatch cannot
   replace the main Sonar project.
2. A licensed self-hosted WSL runner invokes Windows PowerShell 7 and
   `Tools~/run-unity-coverage.ps1`. This job only executes trusted main code.
3. The wrapper copies the package and an isolated consumer template to a fresh
   project. The template and the user's Unity project are never modified by a run.
4. Unity runs the selected EditMode regression fixtures with Code Coverage enabled.
   Configured model cases exercise actual FBX inputs; configured GPU readbacks
   exercise independent screen acceptance. This is separate from the regular
   GitHub-hosted Unity test workflow's licence-dependent result.
5. The artifact contains raw OpenCover reports, NUnit results, the Unity log, and
   SHA-256 fingerprints of every package C# source in Editor, Tests and Runtime.
6. `Tools~/sonar-unity-coverage.py` requires passing tests, actual executed points,
   and an exact source fingerprint match. It merges observed sequence-point visits
   and preserves uncovered lines in Sonar generic coverage XML.
7. The main scanner imports that XML with `sonar.coverageReportPaths`. A missing,
   failed or stale measurement fails the scan instead of substituting coverage.

Raw sequence points are measured by Unity, not inferred from test names or Git
changes. The server's final `new_coverage` value is authoritative; a local Git-diff
coverage estimate can differ from Sonar's new-code period.

## Runner configuration

Set these repository variables for the host selected by `CI_RUNNER`:

| Variable | Purpose |
| --- | --- |
| `UNITY_COVERAGE_EDITOR_PATH` | Absolute Windows path to an activated Unity editor. |
| `UNITY_COVERAGE_TEMPLATE_PROJECT` | Isolated consumer containing Assets, Packages and ProjectSettings; includes the package in `testables`. |
| `UNITY_COVERAGE_POWERSHELL` | PowerShell 7 executable reachable from WSL; defaults to `pwsh.exe`. |
| `UNITY_COVERAGE_MODEL_CASES` | Optional absolute Windows JSON path accepted by `-meshlabLodProjectCases`. |
| `UNITY_COVERAGE_SCREEN_INPUT` | Optional absolute Windows directory accepted by `-meshlabLodScreenInput`. |

The consumer must resolve `com.unity.testtools.codecoverage` (1.2.7 is the wrapper's
fallback) and the test fixtures' existing dependencies. Keep host fixtures and any
local dependency copies outside managed worktrees. The configured host stores
these under `D:/Temp/UnityMeshLab-CI`; generated per-run projects live separately
under `D:/Temp/UnityMeshLab-Coverage` and only the wrapper's own GUID directory is
removed. The checked-out package dependency is replaced in the per-run copy.

Model FBX files remain local to the runner. They are not uploaded with coverage.
The checked-in tests, host case manifest and recorded source hashes define the model
selection; do not substitute unrelated geometry to satisfy a coverage threshold.

For local execution, pass `-Root`, `-TemplateProject`, `-UnityPath`, `-Output`, and
the same optional model/readback paths to the PowerShell wrapper. Use a separate
output directory per run. Import with:

```sh
python3 Tools~/sonar-unity-coverage.py --root . --bundle /path/to/output --output /path/to/output/coverage.xml
```

The wrapper uses Unity's documented batchmode coverage flags:
[Unity Code Coverage batchmode](https://docs.unity3d.com/Packages/com.unity.testtools.codecoverage@1.2/manual/CoverageBatchmode.html).
The scanner input uses [Sonar generic coverage](https://docs.sonarsource.com/sonarqube-server/analyzing-source-code/test-coverage/generic-test-data/).

## Diagnose a future failure

Dispatch `sonar-backlog-report.yml` and inspect the uploaded `*.quality-gate.json`,
`*.new-issues.json`, and `*.new-coverage.json` alongside the full backlog. This
separates new-code gate failures from legacy issues. Tokens are supplied separately;
the API helper retries authentication with its existing token fallback.

For coverage failures, inspect `unity-main-coverage` first: NUnit results, Unity
log, raw reports, source fingerprints, and normalization error. For issue failures,
fix the reported code. Do not lower thresholds, change issue status, suppress rules,
or add exclusions to turn the gate green.
