# Review Rules — Claude Review Mode

When Claude is invoked for PR review (not implementation), follow these rules.

## When to Request Claude Review

- Public API changes (method signatures, types, serialized fields)
- Serialization/versioning changes (sidecar format, schema bumps)
- Complex editor logic (LODGroup lifecycle, mesh lifecycle, Undo chains)
- Package version migrations
- Multi-file refactors across asmdef boundaries
- Changes to native plugin interop (C# ↔ C++ ABI)

## Review Checklist

### Package Integrity
- [ ] `package.json` version follows semver
- [ ] `asmdef` references correct — no circular deps, correct platforms
- [ ] Define symbols (`LIGHTMAP_UV_TOOL_FBX_EXPORTER`) match `versionDefines`
- [ ] No Runtime ↔ Editor dependency leaks

### Backward Compatibility
- [ ] No renamed/removed public API without changelog entry
- [ ] Serialized field types/names unchanged (or migration provided)
- [ ] Sidecar assets (`_uv2data.asset`) remain loadable after changes
- [ ] Existing `.meta` GUIDs preserved

### Editor Safety
- [ ] All scene modifications use Undo
- [ ] `RestoreWorkingMeshes()` called before context switches
- [ ] `ForceLOD(-1)` reset before dropping LODGroup reference
- [ ] Temporary meshes destroyed when no longer needed
- [ ] No static state that survives domain reload without cleanup

### Performance
- [ ] No allocations in `OnGUI` / `OnSceneGUI` hot paths
- [ ] No `GetComponentsInChildren` in per-frame code (ok in one-shot actions)
- [ ] Large operations show progress bar or run async

### Native Interop
- [ ] C# marshalling matches C++ function signatures
- [ ] `unsafe` blocks minimal and correct
- [ ] Platform-specific paths handled (Windows/Linux/macOS)

### Documentation & Tests
- [ ] New public API has XML doc comments
- [ ] CHANGELOG.md updated for user-visible changes
- [ ] Samples still work after changes (if applicable)

## Review Output Format

For each finding, report:
- **Severity**: P0 (blocker), P1 (should fix), P2 (nice to have)
- **File:Line**: exact location
- **Issue**: one sentence
- **Suggestion**: concrete fix

Skip praise and obvious observations. Focus on things that could break.
