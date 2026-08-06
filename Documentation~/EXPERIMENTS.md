# Transfer Pipeline — Experiments & Lessons

> **Обновлять этот документ при каждом эксперименте с transfer pipeline.**
> Последнее обновление: v0.15.39 (2026-04-07)

## Эксперимент 2026-08-06 — безопасные размеры oversampled atlas

- **Проблема:** умножение `resolution × internalOversample` в `uint` и расчёт
  стоимости pack в `long` могли переполниться до вызова native xatlas.
- **Изменение:** размеры вычисляются через `ulong` и отклоняются при выходе за
  диапазон `uint`; стоимость pack насыщается до `long.MaxValue`, чтобы лимит
  всегда отклонял чрезмерный atlas.
- **Ожидание/проверка:** штатные разрешения не меняются; переполняющиеся значения
  завершают repack с ошибкой до native pack.

## Правила экспериментов

1. Один PR = одно изменение. Не наслаивать фиксы.
2. Тестировать сначала на простой модели (куб, симметричный объект).
3. Проверять регрессии на сложной модели (Playground LODGroup).
4. Документировать результат здесь ДО мержа.
5. Если ломает — реверт. Не компенсировать другим фиксом.

## Исправление 2026-08-06 — исключительный доступ к native xatlas session

- **Проблема:** async repack оставляет Editor отзывчивым, поэтому параллельный
  вызов мог заменить или уничтожить единственный глобальный `s_atlas`, пока
  background task ещё выполнял native pack.
- **Изменение:** все managed-входы в xatlas session используют общий atomic
  fail-fast guard; повторный вызов отклоняется до `xatlasCreate`, а guard
  освобождается в `finally` после `xatlasDestroy`.
- **Ожидание/проверка:** обычные sync/async repack сохраняют прежний результат;
  перекрывающиеся вызовы больше не входят в native bridge одновременно.

## Эксперимент 2026-08-06 — Линейная фильтрация overlap-ячеек

- **Проблема:** попарная проверка всех face в каждой ячейке сетки имела худший случай
  `O(gridCells * faces²)` и позволяла патологическому UV-мешу надолго блокировать Editor.
- **Изменение:** число face, имеющих общую вершину с текущим face, вычисляется линейно через
  счётчики вершин, пар и троек (формула включений-исключений). Наличие остальных face означает overlap.
- **Сохранённый контракт:** соседние face с общей вершиной игнорируются, а не смежные face в общей
  ячейке по-прежнему помечаются как overlap.
- **Проверка:** добавлены EditMode-тесты для обоих случаев; сложные модели необходимо проверить
  локально в Unity Editor по стандартному протоколу.

## Что НЕ работает (уроки из 5 отклонённых PR и ~10 ревертов)

- **Affine UV0→UV2 mapping** → экстраполяция за пределы шелов → overlap (PR #48, reverted)
- **Включение merged в dedup** → merged вытесняет non-merged → UV2 jumps между LOD (PR #47, reverted)
- **Coverage upgrade to merged** → меняет transfer mode → другие UV2 позиции (PR #47, disabled)
- **Fragment merge sub-grouping по нормалям** → POST-DEDUP DUPLICATE (PR #47, reverted)
- **Centroid matching вместо normal** → 254→380 overlaps (PR #47, reverted)
- **Additive normal penalty** → перевешивает distance для мелких шелов (replaced в PR #51)
- **Per-shell UV0 пороги для merge-detect** → ложные срабатывания (PR #29, #30 — closed)
- **Shape penalty (scale, shear, анизотропия)** для xform vs interp → усложнение без улучшения (PR #34 — closed)
- **Normal-filter fallback с distance порогом** → ломает partition path (PR #28 — closed)
- **Pre-repack UV0 offset** → xatlas игнорирует позицию, только форма (PR #58)
- **Bbox-only overlap detection** → пропускает идентичные позиции SymSplit (PR #58)

## Что работает

- **Clamped barycentrics** > affine mapping — стабильно по конструкции
- **Multiplicative normal penalty** > additive — масштабируется с distance (PR #51)
- **Metadata (symSplitSide)** > алгоритмическое угадывание — 1 int per shell дешевле 5 ревертов
- **Post-hoc iterative fixing** > pre-processing — SymSplit overlap shifting
- **Non-merged приоритет в dedup** — merged shells используют 3D voting, им не нужен specific source
- **Dual overlap detection** (centroid proximity + bbox ratio) — ловит оба типа overlap

## Текущее состояние (v0.15.39)

**Включено:**
- Clamped barycentric interpolation (основной transfer mode)
- Multiplicative normal penalty в FindBestSourceShell
- SymSplit metadata (symSplitAxis, symSplitSide) + reconstruction в transfer
- Dual UV2 overlap detection (centroid + bbox, iterative)
- UV0 perturbation перед xatlas (EPSILON_SCALE)

**Отключено:**
- Coverage upgrade to merged — только диагностика (GroupedShellTransfer.cs:1118)

**Удалено (v0.15.39):**
- BorderRepairAdapter, BorderRepairSolver, SourceMeshAnalyzer
- ShellAssignmentSolver, InitialUvTransferSolver, TransferQualityEvaluator
- Dead Pipeline Settings UI (sourceUv, maxDist, normalAngle, submeshFilter, borderRepair)

## Известные ограничения

1. **Phase 2b dedup** — хрупкая N² логика, iterative rematch может зацикливаться
2. **StripParameterization PCA** — lambda2 может → 0/NaN на вырожденных данных
3. **Пороги не масштабируются** — UV_NEAR, POS_FAR фиксированы, ломаются на нестандартных mesh
4. **Coverage check отключён** — нужен, но при включении меняет transfer mode → UV2 jumps
5. **FindBestSourceShell** — O(N³) worst case без кэширования

## Roadmap

### Фаза 1: Стабилизация (малый риск) ← ТЕКУЩАЯ
- ~~Adaptive thresholds в SymSplit (масштабировать по mesh/UV bounds)~~ — DONE (v0.15.47)
- PCA stability в StripParameterization (clamp lambda2)
- Epsilon harmonization (нормали → 1e-8f)
- EPSILON_SCALE 0.2% → 2%

### Фаза 2: Визуализация (нулевой риск для pipeline)
- Auto-overlay проблем после трансфера
- Summary badge с расшифровкой

### Фаза 3: Dedup (средний риск)
- Anti-deadlock guard, кэширование FindBestSourceShell

### Фаза 4: Coverage (высокий риск, исследование)
- Реактивация coverage без смены transfer mode

## Эксперимент 2026-04-14 — SymSplit shell-to-shell matching в SplitWithParams

- **Проблема:** эвристика `bestShell` (largest overlap shell) в `SplitWithParams` выбирала самый большой overlap shell, из-за чего на LOD2 с несколькими похожими shell split мог применяться не к ожидаемому shell.
- **Изменение:** добавлен явный descriptor-based идентификатор shell в `SplitParams` (signature + UV centroid/size + faceCount + sourceShellId), запись id на source в `Split(mesh, shells, out outParams)` и поиск target shell по exact signature.
- **Fallback:** если exact signature не найден, применяется nearest descriptor distance с `UvtLog.Warn`.
- **Ожидание/проверка:** сценарий «1 source shell → несколько похожих target shells на LOD2» теперь стабильно берёт shell по descriptor id, а не по количеству face.

### Дополнение (2026-04-14, round 2)
- **Расширение состояния source shell:** в `SplitParams` дополнительно сохраняются `descriptor.stableHash`, `uv0Area`, `boundaryLength`, `worldCentroid`, `worldNormal`, `sourceMirrored`, `sourceGroupId`.
- **Связь между LOD:** matching теперь учитывает `descriptorHash` и `groupId` как первичную связь shell→shell/группа→группа, а затем distance fallback.
- **Фикс бинарного кейса:** параметры теперь пишутся по исходным shell (`symSplitSide == 1`), а не по добавленным shell, чтобы не терять связь с source.

## Эксперимент 2026-04-15 — Полные и воспроизводимые параметры SymSplit в `Split(..., out outParams)`

- **Проблема:** бинарный этап запускался только если `totalSplit == 0`, из-за чего shell без N-fold могли остаться без binary split, а логи/параметры были неполными.
- **Изменение 1 (pipeline):** этапы разнесены явно: `Detect+Apply N-fold` → `Detect+Apply Binary` только по shell, не обработанным N-fold.
- **Изменение 2 (threshold):** для binary split записывается фактический `splitThreshold` из midpoint-votes (без принудительного `0f` при малом числе голосов).
- **Изменение 3 (params):** `SplitParams` добавляется для каждого реально применённого split (N-fold и binary) с сохранением source descriptor state.
- **Изменение 4 (диагностика):** итоговый лог теперь печатает breakdown по параметрам: `total`, `N-fold`, `binary`.
- **Ожидание/проверка:** воспроизведение split-паттерна на target LOD детерминированно при смешанном наборе shell (часть N-fold, часть binary).

### Дополнение (2026-04-15, round 2)
- **Логи этапов:** добавлены явные служебные логи старта этапов `Stage 1/2: Detect+Apply N-fold` и `Stage 2/2: Detect+Apply binary on remaining`.
- **Итоговая диагностика:** итоговый лог `Split params` дополнен `applied splits total`, чтобы сверять число реально применённых split с числом сериализованных `SplitParams`.

## Эксперимент 2026-04-15 — Adaptive `UV_NEAR/POS_FAR` для shell matching (Round 3, decision protocol)

- **Статус:** proposal / A-B validation against legacy (до включения по умолчанию).

### 1) Гипотеза

Фиксированные пороги `UV_NEAR/POS_FAR` хуже переносятся между shell разного масштаба.
Ожидаемые улучшения от adaptive-порогов:
- **Symmetry shell:** меньше ошибочных пар left/right при небольшом drift UV/позиции после split/repack.
- **LOD2+ с похожими shell:** выше точность выбора source shell среди близких кандидатов (одинаковый class/group, но разный размер).

### 2) Формула / эвристика adaptive-порогов

Для каждого source shell считаются признаки масштаба:
- `uvArea` — площадь в UV0;
- `boundaryLength` — длина границы в UV0;
- `uvDiag` — диагональ UV0 AABB;
- `posDiag` — диагональ world/object AABB.

Нормировка в пределах текущей группы (устойчиво к outlier через медиану):
- `sUv = clamp(uvDiag / medianUvDiag, 0.5, 2.0)`
- `sPos = clamp(posDiag / medianPosDiag, 0.5, 2.0)`

Расчёт порогов:
- `UV_NEAR_adaptive = UV_NEAR_legacy * lerp(0.85, 1.35, (sUv - 0.5) / 1.5)`
- `POS_FAR_adaptive = POS_FAR_legacy * lerp(0.80, 1.40, (sPos - 0.5) / 1.5)`

Стабилизаторы:
- глобальный clamp: `UV_NEAR ∈ [0.75x, 1.50x]`, `POS_FAR ∈ [0.70x, 1.60x]` от legacy;
- micro-shell guard: если `uvArea < P10`, применить `UV_NEAR *= 0.9`;
- thin-shell guard: если `boundaryLength / sqrt(uvArea) > P90`, не расширять `UV_NEAR` выше `1.15x` legacy.

### 3) Тестовый набор и метрики vs legacy

**Сцены/модели (обязательный минимум):**
1. Симметричный prop (эталон left/right shell).
2. Playground LODGroup (сложный mixed-кейс пакета).
3. LOD-цепочка с 3+ уровнями, где LOD2/LOD3 содержит похожие shell-кандидаты.
4. Stress-кейс с мелкими fragment shell после dedup/merge.

**Протокол:**
- A/B: `legacy` vs `adaptive` на одинаковом входе;
- 10 повторов на кейс (проверка детерминизма);
- фиксировать seed/порядок загрузки для воспроизводимости.

**Метрики:**
- `correct_match_%` — доля shell с ожидаемым source shell id/group id;
- `fallback_count` — число переходов на descriptor-distance fallback;
- `overlap_count` — финальные UV2 overlaps после post-hoc fixing;
- `rerun_stability_%` — совпадение mapping hash между 10 прогонами;
- `matching_time_ms` — среднее время этапа shell matching.

### 4) Stop/Go критерии

**GO (adaptive становится default), если одновременно:**
- `correct_match_%` на symmetry-кейсе не хуже legacy, и минимум `+3%` на LOD2+ похожих shell;
- `fallback_count` не растёт более чем на `+10%` относительно legacy;
- `overlap_count` не выше legacy на всех эталонных сценах;
- `rerun_stability_% >= 99.5%`;
- `matching_time_ms` рост не более `20%`.

**STOP (оставляем/возвращаем legacy), если выполняется любой пункт:**
- деградация `correct_match_%` на symmetry-кейсе более чем на `1%`;
- рост `overlap_count` хотя бы на одной эталонной сцене;
- `rerun_stability_% < 99%` или зафиксирован недетерминизм выбора target shell;
- рост `matching_time_ms > 20%` без подтверждённого quality-win.

**Решение по умолчанию:**
- до выполнения GO-критериев adaptive держать за feature-flag / экспериментальный режим;
- после 2 последовательных прогонов полного набора без регрессий — переводить в default.

---

## Batch 8-tasks (v0.15.47, 2026-04-14)

### Выполнено

1. **Diagnostic logging** — overlap relocator, FBX export, repack pipeline
   - Per-pair shift axis/magnitude/ratio в `FixOverlappingUv2Shells`
   - Rescale UV2 logging
   - FBX export: pruned children, collision count, material trim logging

2. **CountAabbOverlaps metric** (`UvShellExtractor.CountAabbOverlaps`)
   - O(N²) подсчёт пар с bbox overlap > threshold
   - Логируется pre-repack для каждого mesh

3. **SymSplit adaptive thresholds** (`SymmetrySplitShells.cs`)
   - `POS_FAR` = meshDiagonal * 10% (floor 0.1)
   - `UV_NEAR` = shellUvDiagonal * 5% (floor 0.005)
   - Grid search radius масштабируется с uvNear
   - **Требует тестирования на WateringCan и Playground**

4. **ShellTopology iteration cap** (`GroupedShellTransfer.EnforceShellTopologyOnUv2`)
   - Увеличен с 3 до 5 итераций
   - Per-iteration convergence logging
   - Warning если cap достигнут с fixable vertices

5. **Free-space relocator** (`XatlasRepack.RelocateToFreeSpace`)
   - 128x128 occupancy grid из non-overlapping shell AABBs
   - Поиск свободного прямоугольника для каждого overlapping shell
   - Заменяет Phase 2 brute-force all-pairs shift
   - **Требует тестирования: может ли atlas utilization улучшиться?**

6. **N-fold rotational symmetry detection** (diagnostic only)
   - PCA rotation axis detection
   - UV0 layer counting via grid sampling
   - Логирует обнаруженную N-fold symmetry
   - **Не сплитит — только диагностика. Сплит в отдельном PR**

### Не протестировано (требует Unity)

- Все изменения требуют тестирования в Unity Editor
- Порядок тестирования: простая модель → WateringCan → Carousel → Playground
- Если SymSplit adaptive thresholds ломают существующие модели → revert к fixed values
- Если free-space relocator хуже axis-shift → revert к Phase 2 all-pairs

---

## FBX Export & Collision — Known Issues & Constraints

> Обновлено: 2026-04-09

### AssetPostprocessor вызывает массовый реимпорт
- Наличие `OnPreprocessModel` / `OnPostprocessModel` в пакете заставляет Unity реимпортировать ВСЕ модели при установке.
- На больших проектах (тысячи FBX) это ломает collision meshes (0 вершин).
- **Решение**: `OnPostprocessModel` always compiled; controlled at runtime by `PostprocessorDefineManager.IsEnabled()` (EditorPrefs toggle). `OnPreprocessModel` replaced with static `PrepareImportSettings()`. The package is passive on install — postprocessor early-returns when sidecar mode is off and no transient replay is armed.

### Collision mesh (_COL) — нормали обязательны для FBX Exporter
- Unity FBX Exporter (`ModelExporter.ExportObjects`) не может корректно записать mesh без нормалей → 0 вершин после реимпорта.
- **Решение**: `RecalculateNormals()` перед экспортом. Collision mesh в FBX хранит Position + Normal + Tangent.
- Tangent-ы добавляет FBX Importer при реимпорте — безвредно, MeshCollider их игнорирует.
- Убрать нормали/tangent-ы без поломки экспорта нельзя (ограничение Unity FBX Exporter).

### Collision mesh — isReadable = false
- FBX sub-asset meshes по умолчанию не readable.
- `Object.Instantiate(mesh)` НЕ гарантирует readable копию во всех версиях Unity.
- **Решение**: перед экспортом временно включается `isReadable = true` на ModelImporter, с bypass для постпроцессора. Для overwrite пути `.meta` восстанавливается из backup.

### Sidecar collision entries — не удалять при FBX overwrite
- Старый код удалял весь sidecar (включая collision entries) после overwrite.
- При повторном экспорте collision meshes были недоступны (non-readable FBX sub-assets).
- **Решение**: `ClearUv2EntriesForFbxPaths()` — удаляет только UV2 entries, сохраняет collision entries.

### Convex hull triangle indices — глобальный offset
- `SaveToSidecar()` хранит triangle indices как flattened array. Для multi-hull convex decomposition индексы должны быть rebased к глобальному vertex offset.
- Без rebasing: hull 1+ получает отрицательные индексы → сломанный mesh.

### Import settings (weldVertices и т.д.) безопасно оставлять
- `PrepareImportSettings()` отключает `weldVertices`, `meshCompression`, `meshOptimizationFlags` для корректной работы UV2 remap.
- Эти настройки сохраняются в `.meta` и **не ломают mesh-и** — просто отключают minor оптимизации.
- НЕ нужно восстанавливать после экспорта. Нюк-кнопка НЕ трогает `.meta`.

### AO данные в UV каналах — PreserveUvChannels
- AO записывается в `originalMesh` (рабочая копия), но экспорт берёт `resultMesh` (repacked/transferred).
- `PreserveUvChannels()` копирует UV каналы из source mesh если export mesh их не имеет.
- **Важно**: копировать из ОБОИХ `fbxMesh` (базовые UV) И `originalMesh` (AO и другие модификации).

### Cleanup — опасные операции
- `FixColliders()` → `mesh.Clear()` стирает ВСЕ атрибуты. Не стрипать если mesh shared с Renderer.
- `FixMeshStripUvs()` — vertex colors `(0,0,0,0)` может быть валидное AO (полная окклюзия). Авто-стрип colors убран.
- `SaveAndReimport()` в cleanup — добавлять `bypassPaths` чтобы постпроцессор не вмешался.

## Эксперимент 2026-05-13 — Pre-pack snap к integer atlas pixels (отклонено)

**Гипотеза:** xatlas `PackCharts` применяет unconditional per-chart `ceil(extents)` rescale (xatlas.cpp:8345-8362) — sub-pixel-thin шеллы амплифицируются и ломают uniform density. Если pre-snap'ить UV extents per-shell к integer pixel grid до xatlas, ceil() становится no-op'ом и density сохраняется без форка xatlas.

**Что попробовали (5 коммитов, все ревёрнуты):**
1. `SnapShellsToIntegerPixels(uvFlat, shells, tpu)` — per-shell scale вокруг центроида к ceil(extent×tpu)/tpu.
2. tpu = `effectiveTpu = internalRes × sqrt(coverage)`.
3. Добавление `sqrt(s/p)` фактора (ошибочно — для UvMesh xatlas.cpp:8255 хардкодит `surfaceArea = parametricArea`, т.е. `s/p = 1`).
4. Force `rotateCharts = 0` чтобы snap-grid не сбивалась PCA-вращением.
5. Force `resolution = 0` чтобы избежать xatlas's `maxChartSize` clamp (xatlas.cpp:8363-8385).
6. Перестановка `snap` после `PerturbOverlapShellsUv0` (perturb рескейлил шеллы вокруг чужих центроидов и сбивал snap).

**Результаты на Carousel (149 шеллов, 5 overlap групп, max 92 в одной):**

| Этап | `postAssign maxRatio` (density) | `postCorrection maxRatio` |
|---|---|---|
| Без snap (только Normalize + PostPackCorrection) | ~14× | ~2.95× |
| Snap к tpu (исходник) | 23.11× | 3.76× |
| + sqrt(s/p) factor (wrong для UvMesh) | 23.11× | 4.19× |
| + resolution=0, rotateCharts=0 | 23.11× | 4.06× |
| + snap после perturb (вместо до) | 20.22× | 4.06× |

**Вывод:** snap не работает и делает чуть хуже. Причины математически:
- Для anisotropic shell после snap (sx ≠ sy) xatlas's per-chart scale = `1/sqrt(sx × sy)` (потому что parametricArea меняется на `sx × sy`) — частично откатывает наш snap.
- post-xatlas pixel extent для оси x = `original_x × sqrt(sx/sy)` ≠ integer.
- Isotropic snap (sx = sy = s) полностью undone xatlas (scale = 1/s).
- Единственный способ полностью устранить Stage B amplification — форк xatlas или собственный rect-packer.

**Что оставлено:** `TexelDensityNormalizer.Normalize` (uniform au/a3 = const в UV0) + `PostPackDensityCorrection` (shrink-only post-xatlas). Дает стабильный ~3× density spread на Carousel — лучший достижимый без форка/собственного pack'а.

**Файлы удалены/откачены:** `SnapShellsToIntegerPixels`, `ComputeEffectiveTpu` в `XatlasRepack.cs`, `RepackOptions.snapShellsToIntegerPixels`, `UvToolContext.SnapShellsToIntegerPixels`, UI toggle, force `rotateCharts=0`/`resolution=0` ветки в pack call.

**Что НЕ пробовать снова:**
- Per-shell snap к integer pixel grid в любом виде — xatlas's per-chart scale rebuild параметрической площади после snap всё равно ломает grid.
- "Pre-multiply UV by F в pixel space" — эквивалентно snap по математике (xatlas в обоих случаях пересчитает `sqrt(s/p) × tpu` от новой UV-area и обнулит наш масштаб).
- Передача `texelsPerUnit > 0` с `resolution > 0` — триггерит maxChartSize clamp.

**Что МОЖЕТ помочь (не пробовали):**
- Передавать xatlas **готовый layout** через `xatlasAddMesh` с явными chart definitions, обходя его pack-stage rescale целиком.
- Свой 2D rect-packer на C# поверх Normalize-выровненных шеллов.

## Эксперимент 2026-05-13 — Density spread 14× → 1.17× без форка (commit a218a2b)

**Контекст:** после отката pre-pack snap'а (~5 коммитов) вернулись к чистому `Normalize + xatlas + PostPackCorrection`. Density spread держался на ~3× postCorrection, ~14× pre-correction. Цель — закрыть его не форком.

**Что сработало (в комбинации):**
1. **`UvToolContext.InternalOversample = 1 → 4`** (плюс `RepackOptions.Default.internalOversample = 4`). xatlas pack runs at 1024×1024 internal для пользовательских 256. Sub-pixel шеллы исчезают: shell с UV-extent 0.001 при tpu=909 даёт ≈0.91 px = amp 1.1×, не 4×.
2. **`rotateChartsToAxis = false`** в `RepackOptions.Default`. PCA-rotation перед extents сжимала тонкие шеллы и усиливала `ceil()` amp. Для repack existing UVs (UvMesh путь) она бесполезна. `rotateCharts = true` (90° placement) остаётся.
3. **Удаление `PerturbOverlapShellsUv0`** из обоих pack путей. Это был **главный** убийца: для AddUvMesh xatlas НЕ дедупит charts по UV-сходству — он сегментирует faces по `faceMaterial` (= `shellID`) плюс colocal-UV walk через `vertexToChartMap` (xatlas.cpp:6228-6275). Distinct shellID всегда → distinct charts. Perturb scale `1 + g × strength` cumulative по индексу в группе раздувал sumUV в **~97×** на overlap-группе из 92 шеллов (Carousel), что обрушивало xatlas auto-tpu со 256 до ~104 и кидало все шеллы в sub-pixel regime.
4. **Диагностика `[DensityRisk:prePack]`** в `XatlasRepack.LogStageBRisk` — предсказывает per-shell Stage B amplification (`ceil(extent_px)/extent_px` per axis × per axis) до xatlas. Использует тот же `tpu = sqrt(internalRes² × 0.75 / sumUv)` что xatlas считает внутри. Логирует `subPixel count`, `boost>1.5× count`, `boost>3× count`, top-5 worst шеллов. Точная корреляция: predicted `areaBoost=1.34×` ↔ фактический `postAssign maxRatio=1.27×`.

**Результат на Carousel (149 шеллов, 5 overlap групп, max group 92):**

| Метрика | До | После |
|---|---|---|
| Sub-pixel шеллы | 38/149 | 0/149 |
| `[postAssign] maxRatio` | 20-23× | **1.27×** |
| `[postCorrection] maxRatio` | 3-4× | **1.17×** |
| Шеллов внутри ±10% mean | 10/149 | **149/149** |
| Atlas utilization | 28-34% | **55%** |

**Не пробовать снова:**
- `PerturbOverlapShellsUv0` для UvMesh пути — xatlas не дедупит, perturb только ломает sumUV.
- Per-shell pre-pack snap — xatlas пересчитает per-chart scale от изменённой parametricArea и отменит snap (см. предыдущий эксперимент).
- `rotateChartsToAxis = true` для repack existing UVs — мутирует extents.

## Эксперимент 2026-05-13 — Oversample heuristic pack + atlas-scaled UV2 tolerances

**Контекст:** после `a218a2b` default `internalOversample = 4` сохранил density spread, но поднял внутренний xatlas pack с 256² до 1024². Старый preflight отключал brute force только по `shellCount × internalRes² > 500M`; Carousel-кейс 149 × 1024² ≈ 156M оставался ниже budget, хотя wall-time стал ощутимо хуже. В transfer path часть UV2 tolerances оставалась в normalized-space константах (`0.005`, `0.01`), что при resolved atlas 1389×1360 превращало ~1.3px старого допуска в ~6.8px.

**Изменение 1 (repack):**
- `XatlasRepack.ResolvePackBruteForce()` теперь отключает native `bruteForce` при `internalOversample > 1`, даже если stored UI preference включён.
- Старый safety budget остаётся для `internalOversample = 1`; heuristic safety budget по-прежнему запрещает огромные packs.
- UI делает `Brute force pack` недоступным при oversample выше 1× и явно показывает effective packer = heuristic.

**Изменение 2 (transfer):**
- `RepackResult.atlasWidth/atlasHeight` сохраняются в `MeshEntry.repackedAtlasWidth/repackedAtlasHeight`.
- `GroupedShellTransfer.Transfer()` принимает resolved source atlas size и переводит UV2 pixel margins через `pixels / min(atlasW, atlasH)`.
- Legacy fallback остаётся прежним (`0.005`, `0.01`) для source meshes с existing UV2 или неизвестным atlas size.
- Full pipeline теперь явно пропускает transfer/auto-tune, если нет включённых target LOD meshes, вместо трёх source-only repack попыток с `coverage=0%`.

**Проверка:**
- EditMode red/green: `PackPreflight_DisablesBruteForce_WhenInternalOversampleIsAboveOne`.
- EditMode red/green: `BruteForceOption_IsUnavailable_WhenInternalOversampleIsAboveOne`.
- EditMode red/green: `TransferTargetDetection_IgnoresSourceOnlySelection`.
- EditMode red/green: `Uv2PixelMargin_ScalesFromResolvedAtlasSize`.
- Full model benchmark (Carousel/Playground/WateringCan) в этом checkout не прогнан: тестовые FBX/`BenchmarkReports/` отсутствуют в репозитории. Нужен ручной Unity прогон на suite для финального сравнения `repackMs`, `density spread`, `overlapShellPairs`, `invertedCount`, `texelDensityBadCount`.
