# Code Review — Lightmap UV LOD Transfer v0.12.0

## 1. Общая архитектура

Проект представляет собой Unity Editor-инструмент для переноса UV2 lightmap-координат с LOD0 на остальные LOD-уровни. Архитектура чистая и хорошо разделена на модули:

```
UvTransferWindow (UI) → Pipeline orchestration
    ├── Uv0Analyzer          — анализ и починка UV0
    ├── XatlasRepack          — реупаковка UV0 → UV2 через xatlas
    ├── SymmetrySplitShells   — разделение зеркальных шеллов
    ├── GroupedShellTransfer   — ядро: перенос UV2 shell-to-shell
    ├── TransferValidator      — валидация результата
    └── Uv2AssetPostprocessor — персистенция через FBX sidecar
```

**~13,000 строк C#, 26 файлов, 0 внешних зависимостей** (кроме xatlas/meshoptimizer через native DLL).

---

## 2. Сильные стороны

### 2.1. Продуманный алгоритмический подход
- **Shell-First Matching with Interpolation Primary** — архитектурно верный выбор. Интерполяция ограничена выпуклой оболочкой source UV2, что предотвращает экстраполяцию. Similarity transform используется только как fallback при строго меньшем количестве артефактов.
- **Multi-criteria rescoring** (v0.11.0) — 4-факторная оценка (UV0 coverage 35%, normals 30%, area ratio 20%, 3D distance 15%) хорошо решает проблему неверного начального matching по одному 3D centroid.
- **Cross-source overlap prevention** (v0.11.2-0.11.3) — многоуровневая защита от перекрытий: xform bounds check, constrained search, overlap guard для all-source fallback.

### 2.2. Robustness и edge cases
- **Deterministic replay** (v0.12.0) — сохранение remap-таблицы вместо повторного запуска MeshOptimizer гарантирует идентичный результат при reimport FBX.
- **Fingerprint validation** — FNV-1a хеш по позициям и UV0 обнаруживает изменения FBX после создания sidecar.
- **Множественные fallback-пути**: replay → legacy + remap → position remap → nearest-unused → nearest-any.

### 2.3. Оптимизации
- **BVH** для ускорения UV0 и 3D-поиска (2D и 3D варианты).
- **Spatial hash** для weld-операций (27-cell neighborhood, ~10-50× ускорение).
- **Subsampling** при shell matching (`kMaxSampleVerts = 32`).

### 2.4. Quality assurance
- Валидация на каждом этапе: UV0 analysis, transfer validation, overlap detection.
- Визуализация: checker preview, color-coded canvas, подробное логирование.

---

## 3. Обнаруженные проблемы

### 3.1. Критические

#### 3.1.1. `UvTransferPipeline.cs` использует legacy-пайплайн
**Файл**: `UvTransferPipeline.cs:145-195`

Pipeline.Run() вызывает `ShellAssignmentSolver.Solve()` + `InitialUvTransferSolver.Solve()` + `BorderRepairSolver.Solve()` — это **legacy-код**, который был заменён `GroupedShellTransfer` в v0.5.0. Фактический пайплайн управляется из `UvTransferWindow` напрямую, но `UvTransferPipeline.Run()` остаётся как «мёртвый» entry point, который может ввести в заблуждение.

**Рекомендация**: пометить `UvTransferPipeline.Run()` как `[Obsolete]` или удалить, оставив только структуры данных (`PipelineSettings`, `PipelineResult`), если они используются.

#### 3.1.2. `RepackUv()` игнорирует параметр `faceShellIds`
**Файл**: `XatlasRepack.cs:93-118`

Метод `RepackUv()` принимает `faceShellIds` как параметр, но вообще его не использует — вместо этого вызывает `RepackSingle()`, который сам вычисляет shell ID через `UvShellExtractor`. Это не баг (результат тот же), но сбивает с толку API.

#### 3.1.3. `RepackMulti` — неверный `chartCount`
**Файл**: `XatlasRepack.cs:395`

```csharp
results[m].chartCount = (uint)outVertCount; // per-mesh chart count approximation
```

`outVertCount` — это количество вершин, а не чартов. Это поле используется только для отображения, но даёт неверную информацию.

### 3.2. Значительные

#### 3.2.1. Дублирование кода weld/compact
**Файлы**: `Uv0Analyzer.cs` — `WeldUv0()`, `WeldInPlace()`, `SourceGuidedWeld()`, `UvEdgeWeld()`

Четыре метода содержат почти идентичный блок "compact vertex attributes" (150+ строк каждый):
- Создание compactMap
- Копирование positions, normals, tangents, UV1, colors, boneWeights
- Восстановление submeshes

Это ~600 строк дублированного кода. Любое изменение (например, поддержка UV2-UV7, BlendShapes) требует правки в 4 местах.

**Рекомендация**: извлечь `CompactMesh(Mesh source, int[] remapOrCompactMap)` в отдельный утилитный метод.

#### 3.2.2. UV каналы 2-7 не обрабатываются при weld
**Файл**: `Uv0Analyzer.cs:205-209, 324-328, 589-593`

`WeldUv0`, `WeldInPlace` и `SourceGuidedWeld` копируют только UV0 и UV1, игнорируя каналы 2-7. Если меш имеет данные в UV3+ (например, дополнительные данные для шейдеров), они будут потеряны при weld. `UvEdgeWeld` копирует только UV0 и UV1 тоже.

Для сравнения, `MeshOptimizer.Optimize()` и `Uv2AssetPostprocessor.ReplayOptimization()` корректно обрабатывают все 8 каналов.

#### 3.2.3. `SymmetrySplitShells` — жёстко закодированные пороги
**Файл**: `SymmetrySplitShells.cs:14-16`

```csharp
const float UV_NEAR = 0.01f;
const float POS_FAR = 0.5f;
const float GRID_CELL = 0.01f;
```

`POS_FAR = 0.5` — абсолютное значение в мировых единицах. Для моделей масштаба <0.1 или >100 этот порог будет некорректен. Нужно нормализовать по mesh diagonal (как это делается в `GroupedShellTransfer` для `kGoodDistSq`).

#### 3.2.4. O(n²) overlap detection в `TransferValidator`
**Файл**: `TransferValidator.cs:353-358`

```csharp
if ((long)facesI.Count * facesJ.Count > 500000) continue;
```

Порог 500,000 пар — разумный guard, но для мешей со многими мелкими шеллами всё равно O(shells²) × O(faces²). При 200+ шеллах (типично для complex models) проверка может занять секунды. BVH для triangle-triangle overlap ускорил бы это.

#### 3.2.5. Spatial hash collision risk
**Файлы**: `Uv0Analyzer.cs:962-968`, `EdgeAnalyzer.cs:378-384`

Пространственный хеш использует XOR трёх больших простых чисел:
```csharp
return x * 73856093L ^ y * 19349663L ^ z * 83492791L;
```

XOR-хеширование даёт коллизии для симметричных координат (e.g., `(1,2,3)` и `(3,2,1)` могут коллидировать). Для weld-операций это не критично (дополнительная проверка `VecEqual`), но стоит учитывать при отладке ложных weld-кандидатов.

### 3.3. Мелкие

#### 3.3.1. `DiagnoseLongestEdges` — результат не используется
**Файл**: `XatlasRepack.cs:619-636`

Метод собирает top-N самых длинных UV2 рёбер, сортирует, но ничего не возвращает и не логирует. Чисто диагностический мёртвый код.

#### 3.3.2. `ApplyBorderInset` — три перегрузки
**Файл**: `XatlasRepack.cs:436-473, 665-684`

Три версии `ApplyBorderInset`: `(Mesh, int, uint)`, `(Vector2[], int, uint)`, `(Vector2[], uint, uint, uint)`. Публичная `(Mesh,...)` версия не используется нигде в коде. Две версии для arrays различаются только типами параметров (`int` vs `uint`).

#### 3.3.3. Legacy transfer overload — пустая реализация
**Файл**: `GroupedShellTransfer.cs:1516-1519`

```csharp
public static TransferResult Transfer(Mesh targetMesh, SourceShellInfo[] sourceInfos)
{
    return new TransferResult { uv2 = new Vector2[targetMesh.vertexCount], ... };
}
```

Возвращает пустой массив UV2. Может быть вызван случайно вместо `Transfer(Mesh, Mesh)`.

#### 3.3.4. `Uv2DataAsset.Set()` — 18 параметров
**Файл**: `Uv2DataAsset.cs:188-196`

Метод принимает 18 параметров. Было бы чище передавать готовый `MeshUv2Entry`.

---

## 4. Анализ пайплайна обработки

### 4.1. Текущий (актуальный) pipeline
```
1. Select LODGroup → Collect meshes
2. UV0 Analyze → detect false seams, degenerate tris
3. UV0 Weld (false seams) → MeshOptimizer dedup → UvEdgeWeld
4. SymmetrySplit → split mirrored shells
5. Xatlas Repack (LOD0) → UV0 shells → UV2 atlas
6. GroupedShellTransfer → per-LOD UV2 transfer:
   a. Extract UV0 shells (source + target)
   b. Precompute similarity transforms
   c. Match target → source shells (3D centroid)
   d. Rescore merged shells (multi-criteria)
   e. Deduplicate (one-to-one assignment)
   f. Transfer: interpolation primary, xform fallback, merged 3D-primary
7. Validate → per-triangle classification
8. Apply → sidecar asset + postprocessor
```

### 4.2. Legacy pipeline (не используется, код сохранён)
```
UvTransferPipeline.Run()
  → SourceMeshAnalyzer.Analyze()
  → ShellAssignmentSolver.Solve()
  → InitialUvTransferSolver.Solve()
  → BorderRepairSolver.Solve()
  → TransferQualityEvaluator.Evaluate()
```

**Файлы только для legacy** (можно удалить или перенести в отдельную папку):
- `InitialUvTransferSolver.cs`
- `ShellAssignmentSolver.cs`
- `BorderRepairSolver.cs`
- `BorderPrimitiveDetector.cs`
- `SourceMeshAnalyzer.cs` (частично — `PrepareTarget` может использоваться)
- `TransferQualityEvaluator.cs`

---

## 5. Качество кода

### 5.1. Положительные стороны
- **Исчерпывающие комментарии** в заголовках файлов — описание алгоритма, ссылки на источники.
- **Последовательное именование**: `Uvt*` namespace, `*Solver`, `*Analyzer` — легко ориентироваться.
- **Verbosity-level logging** через `UvtLog` — хорошая практика для отладки.
- **Defensive coding**: bounds checks на индексы (`vi >= uv0.Length`), null-проверки, try-finally для xatlas native.
- **Подробный CHANGELOG** — каждое изменение документировано с контекстом и reasoning.

### 5.2. Области для улучшения
- **UvTransferWindow.cs — 2,029 строк**. Смешивает UI (OnGUI, tab rendering), pipeline orchestration (ExecRepack, ExecTransfer), и GL rendering. Разделение на partial classes или выделение renderer/controller улучшило бы поддерживаемость.
- **Нет unit-тестов**. Для математически интенсивного кода (similarity transform, BVH, point-to-triangle) тесты особенно ценны. Regression tests для transfer quality предотвращали бы откат при рефакторинге.
- **Magic numbers** в `GroupedShellTransfer`:
  - `kMaxSampleVerts = 32`
  - `kConsistencyThresh = 0.02f`
  - `kUv0DistantThresh = 0.05f`
  - `kBackfaceDot = 0.3f`
  - `kOobMargin = 0.005f`

  Вынести в struct `TransferSettings` для удобства настройки.
- **Только Windows x64**: prebuilt DLL только для Windows. macOS/Linux пользователи Unity не смогут использовать инструмент.

---

## 6. Рекомендации по приоритету

### Высокий приоритет
1. **Исправить chartCount в RepackMulti** — одна строка, но вводит в заблуждение.
2. **Добавить UV2-7 каналы в weld-операции** — потеря данных при наличии дополнительных UV-каналов.
3. **Нормализовать POS_FAR в SymmetrySplitShells** по mesh diagonal.

### Средний приоритет
4. **Извлечь общий метод compact/remap mesh** из 4 weld-функций.
5. **Пометить/удалить legacy pipeline** (`[Obsolete]` или перенести в `Editor/Legacy/`).
6. **Добавить базовые unit-тесты** для ComputeSimilarityTransform, PointToTri2D/3D, UvShellExtractor.

### Низкий приоритет
7. **Разделить UvTransferWindow** на partial classes (UI / pipeline / rendering).
8. **Собрать native DLL для macOS/Linux** (CMake уже настроен, нужно добавить CI).
9. **Вынести magic numbers** в настраиваемый struct.
10. **Удалить мёртвый код**: `DiagnoseLongestEdges`, неиспользуемый `ApplyBorderInset(Mesh,...)`, legacy `Transfer(Mesh, SourceShellInfo[])`.

---

## 7. Заключение

Проект v0.12.0 находится в **хорошем состоянии**. Основной алгоритм (GroupedShellTransfer) тщательно проработан и последовательно улучшался через 20+ итераций. Система валидации и визуализации позволяет оперативно диагностировать проблемы. Deterministic replay и fingerprint validation обеспечивают надёжную персистенцию.

Основные риски: дублирование кода weld, потенциальная потеря UV-каналов 2-7 при weld, и legacy code, который может сбить с толку. Автоматизированное тестирование — главный пробел, который стоит закрыть для уверенного рефакторинга.
