---
name: unity-ci-validation
description: Author and debug Unity CI workflows for UPM packages — GameCI actions, batch mode EditMode and PlayMode test runs, license activation, NUnit result parsing, and release gates on SemVer. Use when creating or editing .github/workflows/*.yml for Unity, diagnosing a failed CI run, or wiring semantic-release / GitHub Release automation. Knows -batchmode -nographics -runTests -testPlatform -testResults conventions and Application.isBatchMode guards.
paths: [".github/workflows/**/*.yml", ".github/workflows/**/*.yaml"]
---

# unity-ci-validation

Authoring and debugging skill for Unity CI. Covers GameCI workflows, batch-mode invocations, license activation, and release automation. This skill does not edit C# code; use `unity-editor-tooling` for that. The companion audit is `repo-auditor`.

## Scope and delegations

Covered here:

- `.github/workflows/*.yml` skeletons for test and release.
- GameCI action versions and inputs.
- License activation (personal and professional).
- Batch-mode command-line flags.
- NUnit XML result parsing and GitHub Check annotations.
- Release automation via semantic-release or manual tag push.

Delegated elsewhere:

- **Package metadata checks** → `unity-package-reviewer`.
- **Code-level test authoring** → `unity-serialized-workflow`, `unity-editor-tooling`.
- **Namespace migration** → `migration-and-refactor-planner`.

## GameCI skeleton (test + release)

Pin action versions to exact tags. Floating-version references are a common CI-flake source.

```yaml
name: ci
on:
  push:
    branches: [main]
    tags:   ['v*']
  pull_request:
    branches: [main]

jobs:
  test:
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        unity-version: ['2021.3.45f1', '2022.3.50f1', '6000.0.33f1']
        test-mode:     ['editmode']
    steps:
      - uses: actions/checkout@v4
        with: { lfs: true }

      - uses: actions/cache@v4
        with:
          path: Library
          key: Library-${{ matrix.unity-version }}-${{ hashFiles('Packages/**/*.json', '**/*.asmdef') }}
          restore-keys: |
            Library-${{ matrix.unity-version }}-
            Library-

      - uses: game-ci/unity-test-runner@v4
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
        with:
          unityVersion: ${{ matrix.unity-version }}
          testMode:     ${{ matrix.test-mode }}
          artifactsPath: artifacts/${{ matrix.unity-version }}-${{ matrix.test-mode }}
          githubToken:   ${{ secrets.GITHUB_TOKEN }}

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results-${{ matrix.unity-version }}-${{ matrix.test-mode }}
          path: artifacts/**/*.xml

  release:
    needs: test
    if: startsWith(github.ref, 'refs/tags/v')
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: softprops/action-gh-release@v2
        with:
          generate_release_notes: true
```

Rules:

- Matrix pins full Unity version strings (major.minor.patchNfN). Floating `2022.3` is not acceptable.
- Library cache key includes `*.asmdef` hash so asmdef churn invalidates cache automatically.
- `fail-fast: false` so one failing matrix row does not abort the others.
- Release job gates on `refs/tags/v*` — the test job runs on every push and PR, the release job only on tag push.

## License activation

> **Important.** As of 2024, Unity disabled manual activation of Personal licenses. The legacy `game-ci/unity-request-activation-file` → upload `.alf` to `license.unity3d.com` → download `.ulf` flow only works for **Pro / Plus / Enterprise** seats. Personal-tier projects must either reuse a locally-activated `.ulf` (machine-bound — usually fails on a GitHub-hosted runner), use a self-hosted runner with a pre-activated Unity install, or accept that Unity test execution is unavailable in CI on this tier.

### Personal (free) tier — three realistic paths

1. **License-gate the workflow** (default for free-tier repos). Skip the Unity job cleanly when no credentials are present so CI stays green; metadata, `.meta`, and namespace checks still run on every push and PR. Recipe in the next section.
2. **Reuse a locally-activated `.ulf`.** Possible to try, low success rate. Personal `.ulf` files are bound to the original machine's hardware ID; GitHub-hosted runners typically fail validation. Worth attempting once before giving up.
   - Windows: `C:\ProgramData\Unity\Unity_lic.ulf`
   - macOS: `/Library/Application Support/Unity/Unity_lic.ulf`
   - Linux: `~/.local/share/unity3d/Unity/Unity_lic.ulf`
   - `base64 -w0 Unity_lic.ulf` → paste as `UNITY_LICENSE` repository secret.
3. **Self-hosted runner.** A workstation that already has Unity Personal activated runs the GitHub Actions runner agent. Bypasses the licensing gate entirely. Overkill for small packages, sensible for active solo projects.

### Professional / Plus tier

- Set `UNITY_SERIAL`, `UNITY_EMAIL`, and `UNITY_PASSWORD` repository secrets; `unity-test-runner` picks them up automatically.
- License is checked out at job start and returned at job end; concurrent jobs share the same seat sequentially (GameCI handles serialization).
- Treat the serial as long-lived but rotate immediately if leaked.

### License-gate pattern (canonical for any tier)

Probe the secret presence in a separate job; gate the test matrix on the result. This is the only pattern that keeps CI green across all of: PR from a fork (no secret access), fresh clone before secret setup, and Personal-tier repos that cannot activate at all.

```yaml
jobs:
  check-license:
    runs-on: ubuntu-latest
    outputs:
      has-license: ${{ steps.check.outputs.has-license }}
    steps:
      - id: check
        env:
          HAS_PERSONAL: ${{ secrets.UNITY_LICENSE != '' }}
          HAS_PRO: ${{ secrets.UNITY_SERIAL != '' && secrets.UNITY_EMAIL != '' && secrets.UNITY_PASSWORD != '' }}
        run: |
          if [ "$HAS_PERSONAL" = "true" ] || [ "$HAS_PRO" = "true" ]; then
            echo "has-license=true" >> "$GITHUB_OUTPUT"
          else
            echo "has-license=false" >> "$GITHUB_OUTPUT"
            echo "::notice::Unity license secrets not configured — skipping test job."
          fi

  editmode:
    needs: check-license
    if: needs.check-license.outputs.has-license == 'true'
    # ... game-ci/unity-test-runner step ...

  summary:
    needs: [check-license, editmode]
    if: always()
    runs-on: ubuntu-latest
    steps:
      - run: |
          # 'skipped' is OK (no license); only real 'failure' fails the check.
          [ "${{ needs.editmode.result }}" = "failure" ] && exit 1 || true
```

Notes:

- `secrets.X != ''` evaluates BEFORE secret masking, so the comparison works even when the secret is empty/unset. The masked value never appears in logs.
- Pass the comparison through an `env:` block, not a direct `${{ secrets.X }}` interpolation in `run:` — keeps the runner shell from ever touching the raw secret.
- The `summary` job exists so the overall workflow check is green when the matrix legitimately skips. Without it, a `needs` graph with all-skipped leaves shows as "skipped" on the PR check, which some branch-protection rules interpret as failure.

### Secrets layout (minimum scopes)

- `UNITY_LICENSE` (personal) OR `UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD` (professional).
- `GITHUB_TOKEN` is automatic for standard actions.
- Never log secret contents. The GameCI action redacts by default; custom `run:` steps must not `echo` them. Use the env-var indirection pattern shown above.

## Batch-mode invocations

When a workflow invokes Unity directly (outside GameCI), use the canonical flag set:

```
unity -batchmode -nographics -quit \
      -projectPath "$GITHUB_WORKSPACE" \
      -logFile - \
      -executeMethod SashaRX.<Package>.Editor.Ci.BuildExport.Run
```

Test runner mode:

```
unity -batchmode -nographics -quit \
      -projectPath "$GITHUB_WORKSPACE" \
      -runTests -testPlatform EditMode \
      -testResults artifacts/editmode.xml \
      -logFile -
```

Flags:

- `-batchmode` disables interactive UI; required for CI.
- `-nographics` disables GPU initialization; required on headless runners.
- `-quit` exits after the method / test run completes.
- `-logFile -` streams the log to stdout so GitHub Actions captures it.
- `-testResults <path>` writes NUnit XML to the specified path.

Exit codes: `0` success, `2` test failure, `3` build failure, `4` other failure. Treat any non-zero as failure unless explicitly mapped.

## `Application.isBatchMode` guard

Any `-executeMethod` entry point must guard against interactive dialogs. `EditorUtility.DisplayDialog` blocks indefinitely in batch mode unless gated.

```csharp
public static class CiBuildExport
{
    public static void Run()
    {
        if (!Application.isBatchMode)
        {
            // Interactive path for local testing
            RunInteractive();
            return;
        }

        try { RunHeadless(); }
        catch (Exception e)
        {
            Debug.LogError($"[CI] {e}");
            EditorApplication.Exit(1);
        }
    }
}
```

Do not call `EditorUtility.DisplayDialog`, `EditorUtility.DisplayCancelableProgressBar`, or any `System.Windows.Forms` API from a batch-mode code path.

## NUnit result parsing

GameCI's `unity-test-runner` emits NUnit 3 XML at the `artifactsPath`. To surface failures as GitHub Check annotations, consume the XML in a follow-up step:

```yaml
      - name: Annotate failing tests
        if: always()
        uses: dorny/test-reporter@v1
        with:
          name: Unity ${{ matrix.unity-version }} ${{ matrix.test-mode }}
          path: artifacts/**/*.xml
          reporter: dotnet-trx
```

Alternative: upload the XML as an artifact (shown in the skeleton above) and download for post-mortem.

## Release automation

**Option A: manual tag push.**

1. Maintainer bumps `package.json` `version` and `CHANGELOG.md`, commits, and pushes.
2. Maintainer tags: `git tag v<version> && git push origin v<version>`.
3. The `release` job gates on `refs/tags/v*` and creates a GitHub Release with generated notes.

**Option B: semantic-release.**

Use `cycjimmy/semantic-release-action@v4` with a Unity-specific plugin list in `.releaserc.json`:

```json
{
  "branches": ["main"],
  "plugins": [
    "@semantic-release/commit-analyzer",
    "@semantic-release/release-notes-generator",
    ["@semantic-release/changelog", { "changelogFile": "CHANGELOG.md" }],
    ["@semantic-release/npm", { "npmPublish": false }],
    ["@semantic-release/git", { "assets": ["CHANGELOG.md", "package.json"] }],
    "@semantic-release/github"
  ]
}
```

`"npmPublish": false` is mandatory — Unity packages are not published to npm; they are consumed via git URL or OpenUPM.

## Debugging failed runs

- **License activation failure on Personal tier** → expected as of 2024; Unity disabled manual `.alf`→`.ulf` activation for Personal seats. Use the license-gate pattern in §License activation, or move to a self-hosted runner. A locally-extracted `.ulf` from `Unity_lic.ulf` may work but is hardware-bound and usually fails on GitHub-hosted runners.
- **License activation failure on Pro tier** → check the secret's base64 encoding is single-line, no trailing newline; verify `UNITY_EMAIL` and `UNITY_PASSWORD` belong to the same Unity ID seat that owns the serial.
- **EditMode job fails in ~5 seconds** → GameCI's fast-fail when license env vars are all empty. Add the license-gate job (§License activation) so the matrix skips instead of failing.
- **Library cache miss on every run** → the cache key includes a hash of a file that changes every run; narrow the hash inputs.
- **Tests pass locally but fail in CI** → check `Application.isBatchMode` guards; check that tests do not depend on scene objects that exist only when the editor opens interactively.
- **`EditorApplication.Exit(0)` reached too early** → a test assembly failed to compile and batch mode short-circuits; check the log for `error CS`.
- **Release job runs but nothing publishes** → `npmPublish` left at `true` or tag format mismatched `v*`.

## Further reading

- `_checklists/package-release.md`
- `_shared/anti-patterns.md` (item 19)
- `unity-package-reviewer/SKILL.md`
