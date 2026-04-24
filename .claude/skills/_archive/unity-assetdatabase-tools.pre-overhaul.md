---
name: unity-assetdatabase-tools
description: Используй при написании AssetPostprocessor, батчинге AssetDatabase-операций с StartAssetEditing, управлении импортом или генерации HideAndDontSave-ассетов.
---

# AssetDatabase Tools

Батчинг: StartAssetEditing в try/finally. AssetPostprocessor: GetPostprocessOrder явно + bypass HashSet. Progress bar: DisplayCancelableProgressBar + ClearProgressBar в finally. ЗАПРЕЩЕНО: StartAssetEditing без try/finally, Refresh внутри цикла, Resources.Load в Editor, FindAssets без t: фильтра.
