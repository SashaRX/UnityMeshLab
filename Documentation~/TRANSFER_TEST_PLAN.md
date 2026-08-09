# UV Transfer — Debug & Test Plan

> Цель: систематически локализовать, **где** и **на каком классе ассетов** transfer
> портит lightmap UV2 — по объективным метрикам, а не на глаз. Работает для обоих
> поколений: классический (`GroupedShellTransfer` + `XatlasRepack`) и каскад
> (`HierarchicalRepack`, стадии A→F).
>
> Компиляцию/бенч гоняет пользователь в Unity; артефакты приходят под
> `bench_*/{case}/hier/`. Принцип лестницы: **простое → сложное**, каждый шаг ловит
> один класс дефекта. Не переходить на следующую ступень, пока текущая не зелёная.

## 0. Инварианты перед любым тестом

- **Детерминизм.** Один и тот же вход два прогона → **идентичный** UV2 (hash
  `finalUv2`/`proxyUv2`). Если нет — сначала чинить недетерминизм (seed LCG,
  порядок renderer'ов, union-find), отладка иначе бессмысленна.
- **Один рычаг за раз.** Менять один параметр `Options`/один фикс на прогон
  (правило EXPERIMENTS.md). Ломает — реверт, не компенсировать.
- **A/B baseline.** Каждый прогон сравнивать с зафиксированным baseline (тот же
  ассет, дефолтные `Options`). Метрика без baseline — шум.

## 1. Лестница фикстур (по возрастанию сложности)

| # | Фикстура | Что изолирует | Зелёный критерий |
|---|---|---|---|
| **F0** | Unit cube (6 квадов, 1 shell, без симметрии) | тождество: тривиальный transfer не должен ничего портить | overlap=0, inverted=0, tpuSpread≈1.0, unplaced=0 |
| **F1** | Симметричный бокс / mirrored prop | SymSplit left/right, mirror-handling | нет duplicate-UV2, симметричные shell'ы в РАЗНЫХ областях атласа |
| **F2** | Одиночный цилиндр / дуга (кривой shell) | фолдинг, вырождение планар-проекции, кривой canonical | inverted=0, overlapPct<1%, чарт — не «бабочка» |
| **F3** | Ряд одинаковых инстансов (гвозди/доски, общий UV0) | fragment-merge, дубль-UV2 (дефект T1 аудита) | uv2DuplicatePairs=0, каждый инстанс — свой слот |
| **F4** | 2-LOD простой (куб LOD0/LOD1) | кросс-LOD согласованность (базовая) | xLodContainedPct>95%, домен в одной области на обоих LOD |
| **F5** | Тонкая двусторонняя панель (2 LOD) | Stage D без normal-gate сливает перед/зад (аудит) | перед/зад — РАЗНЫЕ домены, overlap=0 |
| **F6** | Full suite: Gazebo, Carousel, Playground, WoodenBox | реальная сложность (арки, обод 92-shell, трубы, панели+трим) | пороги §4 по каждому кейсу |

F0–F5 — синтетические, делаются за минуты в Unity, ловят 80% регрессий до дорогого F6.

## 2. Пороговые гейты (объективные, из `stage_e_metrics.csv`)

Каскад пишет per-LOD строку. Гейты pass/fail:

| Метрика | Плоская геометрия (WoodenBox/Gazebo) | Кривая/плотная (Carousel/Playground) |
|---|---|---|
| `unplacedFaces` | **0** (>0 = дыра placement'а, Stage E1) | **0** |
| `invertedFaces` | **0** | ≤ 0.5% faces |
| `degenUvFaces` | ≤ 0.1% | ≤ 1% |
| `oobVerts` | **0** | **0** |
| `overlapPctOfCovered` | **< 0.5%** | < 3% |
| `overlapShellPairs` | 0 | небольшое, отслеживать тренд |
| `tpuSpread` (p99/p1) | **< 1.3×** | < 2.0× |
| `xLodContainedPct` | **> 90%** | > 85% |
| `misalignedGroups` | **0** | ≤ 2 |
| `utilizationPct` | > 45% (packEff=0.5) | > 45% |

**Красный флаг > всего:** `xLodContainedPct` низкий = ассет нарушает допущение
«LOD'ы делят UV0-layout» → один бейк НЕ валиден через LOD → каскад тут неприменим,
это не баг кода, а геометрия. Проверять первым.

Классический путь — те же по духу, через `TransferResult`:
`uv2DuplicatePairs` (=0), `compositeBrokenCount` (=0), `severeMismatchCount` (=0),
`shellsUnmatched` (bounded), overlap-пары, inverted.

## 3. Постадийная изоляция каскада (A→F)

Не только end-to-end — проверять артефакт каждой стадии. Порядок диагностики при
красной метрике: идти по стадиям сверху вниз, первая аномалия = корень.

| Стадия | Артефакт | Что проверять |
|---|---|---|
| 1 proxy UV2 | `proxy_uv2_{clean,raw,auto}.png` | UV в [0,1], без грубых наложений; clean — чистая раскладка |
| B classical | `lodN_classical_uv2.png` | каждый LOD разворачивается без мусора |
| 2 samples | `proxy_samples.png` | плотность равномерна, тонкие shell'ы не голодают |
| 3 projection | `lodN_proxy_hits.png` | `missed`≈0, покрытие сплошное (нет чёрных дыр) |
| C shells | `lodN_shells.png` | стабильное число shell'ов, нет дегенератов |
| D cascade | `lodN_groups.png` | один домен = один цвет через LOD; `missed=0`; `tinyOrphan` ограничен |
| E1 pack | `domains_atlas.png` | чарты = реальные UV0-острова (не синусоиды); `packedGroups==groups` |
| E2 cascade | `lodN_final_uv2.png` | домен в ОДНОЙ области на всех LOD; `unplacedFaces=0` |
| E3 metrics | `lodN_overlap.png` + CSV | красных текселей нет на плоском; §2 гейты |
| F meshes | (Apply через меню) | `finalMeshes[li]` face-count == source; submesh'и/материалы целы |

Меню для ручного end-to-end: **`Mesh Lab/Hier/Apply UV2 to Selected LODGroup`**
(диалог теперь показывает overlap px / unplaced / misaligned — хедлайн E3).

## 4. Регрессионные тесты из аудита (`TRANSFER_AUDIT_2026-07-18.md`)

Каждую находку аудита — в воспроизводимый тест:

- **T1 silent zero-UV2.** Ни один shell не должен получить весь UV2=(0,0), кроме
  явного fallback. Тест: прогнать F0–F3, grep лог на `unplacedFaces>0` и Warn
  «no valid domain placement». Отдельно: **отмена посреди transfer** не должна
  оставлять сохранённый занулённый результат (класс.: `GroupedShellTransfer:931`).
- **T2 divergent deepest-picker.** LODGroup, где `renderers[0]==null`, но
  `renderers[1]` валиден. Каскад должен выбрать один LOD согласованно на всех
  стадиях (не рассинхрон meshDiag vs Stage C).
- **T3 масштаб-инвариантность.** Один ассет × 0.01 и × 100 (globalScale) →
  **идентичные** метрики. Расхождение = абсолютные пороги (`1e-5` weld, `0.35`
  UV-bbox, `max(10%diag,0.03)`) ломаются на нестандартном масштабе.
- **T4 brute-force perf.** Ассет 100k faces + группа 50+ shell'ов (Carousel-класс).
  Замерить wall-time Stage 3 проекции и `FindBestSourceShell`. Флаг: >30 с на LOD.
- **T5 fold-over blind (класс.).** Shell, сложенный сам на себя (кривой). Проверить,
  что `CountShellIssues` его НЕ принимает за 0 issues (абс. `1e-10` epsilon).
- **F5 double-sided (каскад).** Тонкая панель: Stage D без normal-gate сольёт
  перед/зад в один домен → проверить `xLodContainedPct` и что перед/зад — разные
  группы в `lodN_groups.png`.

## 5. Свип порогов (когда домены грязные)

`stage_d_sweep.csv` теперь несёт E3-скаляр per-cell (`e3OverlapTexels`,
`e3XLodMinPct`, `e3MisalignedGroups`, `e3UnplacedFaces`). Процедура:
1. Прогнать sweep `cascadeMatchFrac × cascadeMinHits` (дефолт `{0.35,0.5,0.65}×{2,4,8}`).
2. Выбрать ячейку с **min `e3OverlapTexels`** при `e3XLodMinPct` близком к 100 и
   `e3UnplacedFaces=0`. Это объективный winner (раньше свип был только визуальный).
3. Зафиксировать как новый `Options.Default`, пере-прогнать F6, сверить с baseline.

## 6. Дерево триажа (пришёл плохой бенч — что смотреть)

```
overlapPct высокий?
├─ на ПЛОСКОМ ассете (WoodenBox)         → mirror-reuse UV0 между shell'ами;
│                                           смотреть lodN_overlap.png (красное),
│                                           overlapShellPairs → какие shell'ы
├─ только на КРИВОМ (Carousel обод)       → фолдинг canonical'а (Stage E1);
│                                           domains_atlas.png = «бабочки»
xLodContainedPct низкий?                  → ассет не делит UV0-layout LOD'ов
│                                           (не баг — геометрия) ИЛИ Stage D
│                                           разнёс домен; смотреть lodN_groups.png
unplacedFaces > 0?                        → Stage E1 не дал placement группе;
│                                           degenerate-axis LSQ fallback лог
tpuSpread высокий?                        → тексель-density неравномерна;
│                                           проверить texelsPerUnit / S-нормировку
inverted > 0?                             → перевёрнутая намотка при проекции
```

## 7. Контракт артефактов (что присылать мне)

7z с `bench_<timestamp>/{case}/hier/`, обязательно:
- `stage_e_metrics.csv` (главный объективный сигнал),
- `stage_d_sweep.csv` (если гонялся свип),
- `domains_atlas.png`, `lod*_final_uv2.png`, `lod*_overlap.png`, `lod*_groups.png`,
- консольный лог с строками `[HierRepack] Stage E3:` (per-LOD цифры).

По CSV+PNG я локализую стадию-корень без Unity. Сырые метрики > словесное описание.

## 8. Definition of Done для «transfer работает надёжно»

- F0–F5 зелёные по §2 на дефолтных `Options`, детерминированно (2 прогона = hash).
- F6 (4 кейса): `unplacedFaces=0`, `inverted=0`, `overlapPct` в пороге §2,
  `xLodContainedPct>85%` везде.
- T3 масштаб-инвариантность выполнена (× 0.01 / × 100 идентичны).
- Ни одной silent-zero-UV2 (T1) в логах.
- Apply на реальном LODGroup → бейк лайтмапа валиден при переключении LOD (глазами
  один раз, дальше — по `xLodContainedPct`).
