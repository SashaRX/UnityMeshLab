---
name: unity-package-reviewer
description: Используй при ревью диффа, PR или существующего файла на нарушения правил Unity-пакета. Указывает конкретный скилл, чьё правило нарушено.
---

# Unity Package Reviewer

CRITICAL: .mesh, DestroyImmediate без Undo, мутация prefab без scope, StartAssetEditing без try/finally, мутация без Undo, target cast, Editor в Runtime. HIGH: ручной JSON, нет timeout, забытый ApplyModifiedProperties, нет bypass, >50КБ, хардкод. LOW: нет #if, нет progress bar, namespace без префикса.
