# Transfer Pipeline — Experiments & Lessons

> **Обновлять этот документ при каждом эксперименте с transfer pipeline.**
> Последнее обновление: v0.15.39 (2026-04-07)

## Правила экспериментов

1. Один PR = одно изменение. Не наслаивать фиксы.
2. Тестировать сначала на простой модели (куб, симметричный объект).
3. Проверять регрессии на сложной модели (Playground LODGroup).
4. Документировать результат здесь ДО мержа.
5. Если ломает — реверт. Не компенсировать другим фиксом.

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
- Adaptive thresholds в SymSplit (масштабировать по mesh/UV bounds)
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
