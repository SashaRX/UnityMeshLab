# Антипаттерны Unity Editor-пакетов

Консолидированный справочник запретов. Каждый пункт ссылается на скилл с правильным паттерном.

## CRITICAL
1. `.mesh` вместо `.sharedMesh` — unity-undo-prefab-safety
2. Мутация prefab через LoadAssetAtPath — unity-undo-prefab-safety
3. `StartAssetEditing` без try/finally — unity-assetdatabase-tools
4. Мутация без Undo — unity-undo-prefab-safety
5. Прямая мутация target в CustomEditor — unity-serialized-workflow
6. Editor-код в Runtime asmdef — unity-package-architect
7. `DestroyImmediate` без Undo — unity-undo-prefab-safety

## HIGH
8. Ручной JSON-парсинг — unity-editor-tooling
9. `UnityWebRequest` без timeout — unity-editor-tooling
10. `AssetPostprocessor` без bypass — unity-assetdatabase-tools
11. `Resources.Load` в Editor — unity-assetdatabase-tools
12. `Refresh()` внутри цикла — unity-assetdatabase-tools
13. Захардкоженные пути/URL — unity-editor-tooling
14. Забытый `ApplyModifiedProperties` — unity-serialized-workflow
15. Забытый `ClearProgressBar` — unity-assetdatabase-tools
