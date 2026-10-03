# PR #210: advisory review

The report for commit `08ecc22` contained 60 advisory findings: 29 cognitive-complexity findings (S3776), 17 nested-condition findings (S3358), 13 LINQ suggestions (S3267), and one path-delimiter suggestion (S1075).

The refactor separates existing responsibilities rather than changing the packing algorithm: source preview acquisition, inspection-cache pruning, UV render layers, setup and quality panels, individual transfer operations, auto-tune attempts and evaluation, FBX path/backup preparation, hierarchy assembly, post-import replay, native polling, ARAP, and native readback. Hier UV2 canonical-frame normalization and affine fitting retain their original formulas and stage order. Public APIs, serialized fields, assembly/module dependencies and native ABI remain unchanged.

Nested ternaries now use named intermediate values or explicit guards. Duplicate transfer coverage aggregation and working-copy preparation are shared helpers. An unused post-pack statistics scan was removed.

## Deliberate choices

- Keep loops over mesh/vertex/face collections, preview ownership and cache keys. LINQ adapters in these paths add no useful abstraction; the shared cache pruning helper reuses the same removal list across caches. UV0 report iteration uses dictionary values directly where no key is needed.
- Keep `/` when composing AssetDatabase paths. These are Unity asset identifiers rather than OS filesystem paths; `Path.Combine` can introduce backslashes on Windows. FBX filesystem backup paths still use `Path.Combine`.
- Do not suppress findings, change issue status, add exclusions or alter scanner/advisory policy. Remaining advisory findings are visible in the PR check.

## Verification

- Both C# compilation configurations (with/without the FBX exporter define).
- Unity 6000.2.6f2 EditMode regressions: existing GPU/CPU bake, 2D/3D preview, UV workflow, FBX, module and LOD lifecycle suites, plus canonical-frame, affine-fit, degenerate-axis and native Hier UV2 integration tests.
- Tool dependency guard and self-test, undeclared-identifier check, meta GUID uniqueness and diff whitespace checks.
