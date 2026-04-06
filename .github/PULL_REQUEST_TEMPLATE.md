## Summary
<!-- 1-3 bullet points: what changed and why -->

-

## Changed Zones
<!-- Check all that apply -->

- [ ] `Editor/` — Editor tools / UI
- [ ] `Plugins/` / `Native/` — Native plugins
- [ ] `Shaders/` — Compute / render shaders
- [ ] `package.json` — Version / dependencies
- [ ] `.github/` — CI / workflows
- [ ] Docs (`README.md`, `CHANGELOG.md`)

## Checklist

- [ ] `.meta` files present for all new files/directories
- [ ] No Editor ↔ Runtime dependency leaks
- [ ] Undo support for all scene modifications
- [ ] Temporary meshes cleaned up
- [ ] `#if LIGHTMAP_UV_TOOL_FBX_EXPORTER` guards on FBX code
- [ ] CHANGELOG.md updated (if user-visible change)

## Test Plan
<!-- How to verify this works -->

-

## Review Notes
<!-- Anything reviewers should know: risks, trade-offs, open questions -->

