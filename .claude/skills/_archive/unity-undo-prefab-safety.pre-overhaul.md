---
name: unity-undo-prefab-safety
description: Используй при мутации prefab-ассетов, записи Undo-групп, редактировании prefab-оверрайдов или клонировании asset-backed мешей перед модификацией.
---

# Undo и Prefab Safety

Undo-группы: SetCurrentGroupName → GetCurrentGroup → CollapseUndoOperations. RAII: UndoGroupScope. Prefab: EditPrefabContentsScope. Temp-instance: InstantiatePrefab → try {SaveAsPrefabAsset} finally {DestroyImmediate}. Оверрайды: Get/SetPropertyModifications. Mesh: только .sharedMesh + клон. ЗАПРЕЩЕНО: .mesh, LoadAssetAtPath→мутация→SaveAssets, DestroyImmediate без Undo.
