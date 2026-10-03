# PR #210: CodeRabbit and Graphify review

Checked the CodeRabbit reviews of 2026-10-02/03 and Graphify review of 2026-10-02 against `f9b0bef`. Bot output is advisory input; fixes were checked against the actual source.

## CodeRabbit inline findings

| Source | Disposition | Behavior / evidence |
| --- | --- | --- |
| [Editor/Framework/MeshInspection.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170402411) | Fixed | BVH cache compares position/index data and retains matching triangle indices. |
| [Editor/Framework/UvToolHub.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170402416) | Fixed | Content keys and status retain tool/context provenance before applying universal entries. |
| [Editor/Framework/UvToolHub.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170402421) | Fixed | Readable meshes are mapped separately; callbacks and diagnostics keep canonical MeshEntry identity. |
| [Editor/Framework/UvToolHub.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170402429) | Fixed | Status shows the selected UV0-UV7 channel. |
| [Editor/LodPipelineOps.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170402456) | Already fixed | Already fixed by model-root ownership in 08ecc22/222f02d; group isolation tests still pass. |
| [Tools~/check_tool_dependencies.py](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170402464) | Fixed | Guard handles partial/default-access classes, multiline bases and intervening attributes. |
| [Editor/HierarchicalRepack.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170559144) | Fixed | Cancellation skips remaining stages, variants and LOD packs after the in-flight pack drains. |
| [Editor/HierarchicalRepack.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170559147) | Fixed | Both direct Hier native entry points acquire/release the shared xatlas session guard. |
| [Editor/VertexAOBaker.Gpu.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4170559155) | Fixed | Cancellation timeout/exception does not invoke the CPU fallback callback. |
| [Editor/Assets/MeshAssetOperations.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4172504586) | Fixed | Failed writes restore FBX/meta, restore readability, and register no sidecar/replay. |
| [Editor/Framework/MeshViewport3D.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4172504602) | Fixed | Indexed wire edges are deduplicated across submeshes. |
| [Editor/LodGroupUtility.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4172504610) | Fixed | Ambiguous containers require selecting one chain; unrelated LOD names cannot be merged. |
| [Editor/LodGroupUtility.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4172504616) | Fixed | Generated LOD removal records prefab property modifications after SetLODs. |
| [Editor/Tools/RemeshBakeTool.cs](https://github.com/SashaRX/UnityMeshLab/pull/210#discussion_r4172504626) | Fixed | BakeMesh(..., true) compensates renderer scale before the preview matrix applies it. Unity reproduced the original failure. |

The five resolved threads were also checked against the current LOD lifecycle and native packing changes; no additional fix was needed.

## Outside-diff CodeRabbit comments

All four confirmed and fixed in the shared canvas:

- UV channel selection skips missing meshes.
- Channel selection includes the same displayed entries as rendering, including meshes excluded from processing.
- Lightmap picking and inspection markers use the same UV scale/offset helper as rendering.
- Attribute fills honor FillHidden in both 2D and the 3D UV layer.

## Graphify

All five findings were confirmed. FBX rollback, failed-write replay and early-load readability overlap the export thread above. The other two fixes are:

- Narrow export snapshots use computed result meshes while matching the authored FBX sub-asset name.
- Raw reimport clears its own bypass flag in finally and preserves unrelated paths; channel export and readability restoration share this helper.

Across 20 distinct actionable findings (14 open inline threads, four outside-diff comments, two additional Graphify findings), 19 needed fixes and one was already addressed. Completing cancellation also clears the global flag so subsequent operations can run. No findings were suppressed or bot issue statuses changed.

## Verification

- Unity 6000.2.6f2: **251 passed, 0 failed, 0 skipped**, including 26 new review cases. Actual GL rendering, same-object geometry mutation, canonical entry identity, non-uniform skin scale, prefab overrides, injected partial FBX writes/early-load failures, retained backups, GPU watchdog callbacks, occupied native sessions and in-flight pack cancellation were exercised in the isolated project.
- FBX-dependent tests declare the optional package version define in their own assembly so Unity runs them when the exporter is installed.
- C# compilation passes with and without the FBX exporter define. The 11 existing reference-gap substitutions affect build copies only.
- Dependency guard/self-tests, undeclared-identifier scan, metadata coverage/GUID uniqueness and whitespace checks pass.
- Earlier temporary baseline parity checks remain documented in SONAR_ADVISORY_REVIEW.md and are not counted in the 251-test run.

Graphify at f9b0bef was neutral and explicitly verified no PR-changed functions. Its older review reported partial coverage and no reproducing execution or proof for these findings. These statuses are not treated as formal correctness evidence.
