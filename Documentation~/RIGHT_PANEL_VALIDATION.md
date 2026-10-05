# Right panel validation — 1.1.14

Validated on 2026-10-05 with Unity 6000.2.6f2, URP 17.2, D3D11, in an isolated project.

## Behavior

- Source, Remesh, Simplify and Result are preview buttons. Orange marks selection, green marks available output, blue marks a running stage and amber marks changed settings. Missing output is gray and disabled.
- Unwrap/Bake progress belongs to Result; Remesh/Simplify progress belongs to its respective button. Indeterminate work animates without inventing a percentage.
- Each main column has a fixed clipping area. Window resizing preserves preferred widths while constraining the actual layout to the window. Growing the right panel can shrink an oversized left panel.
- Mouse-up outside the window, focus loss and window disposal release a splitter drag.

## Evidence

- Selected EditMode suite: 27 passed, 0 failed, 0 skipped. Includes window-width constraints, right-splitter growth, running-stage mapping and existing preview lighting/normal-packing tests.
- Actual UvToolHub IMGUI layout/repaint replay at widths 500, 800, 1200 and 1900. Started with oversized 900/700 sidebar preferences; no layout/GUIClip errors.
- At width 800, dragging the right splitter 100 pixels grows the visible panel from 220 to 320, shrinks the left panel and releases mouse capture on mouse-up outside the window.
- IMGUI button input replay switches Remesh and Simplify and draws Unwrap progress at 50%. EditorWindow.SendEvent coordinates include the floating-window title bar; initial harness clicks landed there and were corrected.
- License-free compile check: both FBX define variants have 0 errors. Tool dependency and identifier checks pass. git diff --check passes.

Local evidence: `_results~/right-panel/tests.xml`, `gui.txt`, `buttons-final.log`, `final-compile/`.
This validates the isolated Editor replay; the user's live project's panel has not been visually inspected after updating this version.
