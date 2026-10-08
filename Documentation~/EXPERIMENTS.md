# Transfer Pipeline — Experiments & Lessons

> **Обновлять этот документ при каждом эксперименте с transfer pipeline.**
> Последнее обновление: v0.15.39 (2026-04-07)

## Эксперимент 2026-10-08 — Пропорции исходной текстуры и квадратного lightmap

- Исходный случай: текстура 1024×2048 и соответствующие ей UV0. До упаковки UV2
  временная копия получает pixel metric текстуры с учётом tiling материала:
  U × sqrt(width/height), V × sqrt(height/width). ARAP и density работают уже
  в этой метрике; исходный UV0 с исходной размерностью, форматом, stream и raw bytes
  восстанавливается в finally.
  Разные или неизвестные аспекты используемых материалов одного меша не исправляются
  одной общей матрицей. Пустые submeshes не участвуют в выборе метрики.
- Дополнительный дефект подтверждён native-тестом: bridge нормализует координаты
  отдельно по ширине/высоте прямоугольного packed atlas. В UV transfer workflow
  результат переводится в квадратную метрику max(width,height) на стороне C#,
  включая размеры, переданные transfer. Border inset применяется после перевода
  в квадрат, чтобы сохранить указанный отступ в пикселях на обеих осях.
  Native source и binaries не менялись.
- Opt-in Capture сохраняет точные меши/параметры/меж-LOD hints и этапы симметрии,
  repack и transfer. Trace записывает реально оценённые кандидаты без дополнительных
  matching queries; Replay сравнивает baseline и два запуска. Schema 2 выносит большие
  stage/pair данные в checksum-protected details JSON; manifest имеет общий лимит
  writer/reader 64 MiB, payloads — 512 MiB. Replay также выносит массивы каждой пары
  в свой details JSON и держит только summaries в replay.json. Schema 1 по-прежнему читается.
  Добавлены axis stretch
  против 3D и positive-area overlap, включая same-shell пары. Алгоритмы symmetry,
  overlap repair и критерий выбора auto-tune не менялись.
- Проверка после исправления замечаний PR #225: 340 passed / 0 failed / 1 skipped,
  Unity 6000.2.6f2 / DX11. Пропущен fixture совместных Standard/URP normal conventions:
  в изолированном test project отсутствует URP source shader.
  При выключенных ARAP/density source aspect ON даёт anisotropy ≈1, OFF ≈2 после
  repack и transfer; UV0 (включая третий компонент) сохранён. Capture/Replay двух
  LOD с hints совпадает по UV2 hash и mapping; trace on/off не меняет результат.
  Подтверждены прежние shell IDs, scalar UV0 и auxiliary UVs в readable/unreadable
  working copies и snapshots, отказ общей коррекции при textureless used material,
  pixel border после repack, capture payloads суммарно >64 MiB с компактным manifest,
  legacy replay, проверка checksum и обработка ошибок capture/IMGUI.
  После проверки и интеграции Sonar autofix отдельно повторён затронутый набор:
  42/42 tests (transfer capture, shell extraction, geometry helpers, mesh access),
  без skipped. Это subset полного прогона 341 tests, а не другой полный прогон.
  Обе FBX compile variants прошли после autofix.
  Последующие preview/UV0-format/replay-report правки проверены 138/138 tests без
  skipped (transfer capture, mesh access, FBX export, preview LOD switches, viewport),
  Unity 6000.2.6f2 / DX11. Float16 UV0 сохраняет формат, stream и raw bits после
  working copy и native repack/transfer; replay payloads суммарно >64 MiB не растят
  общий summary report.
  Реальная модель пользователя пока не захвачена новым кодом, её overlap/symmetry
  проблема не объявляется исправленной. [Использование и ограничения](TRANSFER_CAPTURE.md).

### Автономное сравнение transfer — 2026-10-08

- Добавлен [бенч зафиксированных входов](TRANSFER_COMPARISON.md): шесть методов,
  семь аналитических случаев, native source preparation на копиях импортированных
  FBX с aspect ON/OFF и symmetry off/legacy/adaptive. Production transfer и выбор
  auto-tune не менялись. Подготовленные входы сохраняются как переносимый capture.
- Изолированный Unity 6000.2.6f2 / DX11 прогон исходных FBX с E: стул и 21 группа
  `Grandma_Cabinet_B_Destructed`, плюс матрица подготовки трёх групп шкафа.
  Всего 41 случай × 6 методов = 246 строк, warmup 1, measured repeats 3 и отдельный
  trace run; UV2/mapping повторяемы, input mesh hashes сохранены.
- В базовой adaptive-подготовке 21 группы текущий grouped transfer дал 75
  positive-area triangle overlap pairs, 74 degenerate faces и 1441 stretched faces.
  Это реальные дефекты качества, а не ошибки выполнения бенча. Source atlas
  `Dpanel_A` уже имеет большую анизотропию (area-weighted ≈542.6) до transfer;
  у `Handles` transfer даёт большую анизотропию (≈58770.2) при source ≈1.9.
  Эти случаи пригодны для раздельной проверки source repack и matching/topology.
- При ARAP/density OFF на трёх выбранных группах source anisotropy с aspect
  OFF в среднем по группам ≈2.08, с aspect ON ≈1.09. Подготовка использует явный
  аспект 4096×8192 из исходного Wood Trim albedo (также 1:2); размеры импортированной
  текстуры могут быть уменьшены без смены аспекта.
- Прогон импортированных sub-assets не воспроизводит UI auto-tune/weld, material
  tiling/conflicts или индивидуальные renderer transforms. Для точного выбранного
  запуска всё ещё нужен next-run capture. Внутри бенча reference для реальных FBX
  является recorded production baseline, не independent ground truth; дефекты
  overlap/symmetry не объявляются исправленными.

## Эксперимент 2026-10-03 — Chart-merge постпроцессинг Unwrap (UV0)

- **UV repair contention 2026-10-05 (продолжение Exp #1):** занятая legacy
  xatlas session ошибочно превращалась в отказ обязательного overlap repair.
  Repair теперь ждёт session на worker с отменой, обычный optional repack
  сохраняет fail-fast. 95 Unity tests passed, включая ожидание, отмену без
  освобождения чужого atlas и сохранение corners; обе FBX compile variants passed.
  Свежий capture 1598 faces / 1024 px воспроизвёл 72 charts / 5 overlap pairs;
  с heuristic packing шесть off/on повторов дали 74 / 47 charts, zero overlaps,
  mean/worst 1.02925/3.07848 и 1.03342/1.68071, одинаковые buffers и corners.
  Original Best packing не завершил первый pack за 6 min 35 s и был остановлен
  только в изолированном harness; его full replay не подтверждён. Production
  packing settings, native source/binaries и quality gates не менялись.

- **Первый trainer 2026-10-04 (подготовка следующего этапа Exp #1):**
  `train.py` перепроверяет independent train/val/test и hashes/geometry, использует
  train-only normalization и val-only best/threshold; модель/optimizer/RNG
  сохраняются для epoch resume. Алиасы одной geometry балансируются как один
  model_group. На synthetic tetrahedra CUDA whole 4 epochs и pause2/resume4
  дают одинаковые model/optimizer/metrics; recovery final JSON не повторяет test.
  Текущий один бюст отклонён до output creation. LearnedSeamBenchmark:
  29 passed / 1 Windows symlink fixture skipped; SeamPlacementBenchmark: 18 passed.
  Это проверка trainer, не обученное художественное UV-решение. Пользовательский
  набор и RTX 3070 Ti ещё не участвовали в quality run. [Проверка](LearnedSeamResearch/TRAINER_VALIDATION.json),
  [запуск на ноутбуке](../Tools~/LearnedSeamBenchmark/README.md#first-local-training-cycle).

- **Геометрическая фаза завершена 2026-10-04 (продолжение Exp #1):**
  PartUV/OptCuts/AutoUV и merge не воспроизвели требуемое расположение швов.
  Проверены SHA/correspondence/winding 19 captures из 22 опубликованных rows;
  18 полных overlap scans и 26 seam V1 rows заново совпали с сохранёнными числами.
  SeamPlacementBenchmark: 18 passed; LearnedSeamBenchmark до trainer:
  21 passed / 1 Windows symlink fixture skipped. Чужие solvers и Unity tests
  заново не запускались. Следующий этап — обучаемые швы с независимыми families;
  два текущих UV-варианта бюста остаются одним asset. [Итог и рендеры](UV_GEOMETRY_EXPERIMENT_FINAL.md).

- **Протокол набора 2026-10-04 (продолжение Exp #1):** основа — существующие
  high-poly → построенные с нуля авторские low-poly, история ручных исправлений
  не обязательна. Оригиналы/native scenes/карты/зависимости сохраняются отдельно
  от производных features; новый `archive.py` копирует явный manifest в свежий
  каталог с input/output SHA256, без parsing или подтверждения полноты сцены.
  Для будущего topology обучения реальные remesh/simplify outputs — основной
  поток, несколько способов порчи — дополнительный. Canonical geometry перед
  порчей, family split до генерации и test на реальных ошибках предотвращают
  часть утечек; обученный результат пока не получен. Уточнено, что seam preparer
  принимает manifold open meshes и сам не распознаёт случайные дырки.
  Текущий LearnedSeamBenchmark: 21 passed / 1 symlink fixture skipped;
  реальная Windows junction-проверка прошла, пользовательские модели не копировались.
  [Полный протокол и ограничения](UV_LEARNED_SEAMS.md).

- **Обучаемые швы 2026-10-04 (продолжение Exp #1):** новый AutoUV baseline
  при cone 35/50/65 сохраняет геометрию, но даёт 51/59/42 intra-chart overlap
  pairs и seam F1 0.213/0.172/0.240. Контроль с точными авторскими cuts, без
  авторских UV coordinates: uniform disk seed → SLIM → square-normalized xatlas,
  zero overlaps, mean/worst 1.105/4.523, fill 50.45% против ручных 69.14%.
  Standalone packing дополнен uniform square normalization, production unchanged.
  Подготовщик labels предотвращает leakage по asset/geometry/family: два UV
  варианта бюста — одна модель. Untrained graph probe 79169 parameters прошёл
  FP32 forward/backward на GTX 980 Ti, peak PyTorch allocated 53.7 MiB;
  обучение/качество модели не проверены, набор пользователю ещё нужно собрать.
  18 seam/control + 10 prepare/graph checks passed, все UV renders сохранены.
  [Методы, доступность релизов, фактические замеры и ограничения](UV_LEARNED_SEAMS.md).

- **Лог из E project 2026-10-04 (продолжение Exp #1):** native source fitting
  и area cleanup воспроизводят одиночное отверстие на том же участке бюста;
  компактный fixture из 8 vertices воспроизводит его при voxel 48. Raw solid
  теперь проверяется до Trim с проверенным retry без solve. Полностью отклонённая
  source-коррекция пробует три меньших шага при прежних gates, лог сохраняет
  attempted movement и RMS/max причины отказа. Cage fit и preview используют
  ту же source-facing/two-sided политику, что bake. Точный UV input из E повторён
  off/on трижды: 84 → 32 charts, zero overlaps, неизменные triangle corners.
  Новый closed 1598-face replay проходит четыре Unwrap после исправления
  budget-aware progressive repair precision; 241 Unity tests passed/skipped0.
  [Причины, ограничения, реальные рендеры и replay](REMESH_LOG_20261004.md).

- **Voxel stair triangles 2026-10-04 (продолжение Exp #1):** source-guided
  fitting, tangent redistribution и локальные edge flips подключены к Simplify
  через Fit to source surface. Source-distance, boundary/Euler и collapse-budget
  gates отклоняют ухудшающие кандидаты; финальная коррекция сохраняет 1886 / 98
  faces и уменьшает slivers 42 → 34 на бюсте, 30 → 21 на диагональном боксе.
  Четыре конечных Unwrap имеют zero overlaps и сохранённые source corners;
  маленькие charts при repair могут повторно паковаться с большей точностью,
  без ослабления stretch gate. Native source/binaries unchanged.
  [Методика, ограничения, реальные сетки и UV](VOXEL_SURFACE_REFINE.md).

- **Крупный piecewise OptCuts shell (продолжение Exp #1):** semantic hierarchy
  делится по surface-area cap и пересекается с mandatory planar regions. При
  cap 20% / symmetric Dirichlet 4.15 крупнейший chart сокращён 63.54% → 16.55%,
  mean/worst 1.260/7.401 → 1.076/3.716, UV-induced sliver faces 20 → 5,
  zero overlaps. Сетка/грани не изменены; 37 intrinsically elongated faces
  остаются. Более строгий Ed 4.05 при тех же partitions даёт 2 UV-induced faces,
  но ухудшает mean stretch до 1.158 и fill до 49.05% (Ed 4.15: 51.13%).
  Сохранены рендеры cap 15/20/30%, Ed 4.05 и сравнение с первым piecewise;
  seam parity всё ещё неполная, в Unity defaults метод не включён.

- **Расположение швов 2026-10-04 (продолжение Exp #1):** сравнение по длине
  швов на одинаковой 1856-face геометрии подтверждает ухудшение recall после
  merge: V1 50.12% → 30.17%, V2 52.45% → 29.80%. Проверены полный PartUV
  native pipeline (ABF, PAMO off, thresholds 1.1/1.25/1.5), OptCuts со свободными
  и обязательными региональными границами, а также geometry-only segmentation.
  Для каждого полного unwrap сохранён PNG и positive-area overlap scan;
  11 новых Python checks проходят, standalone xatlas probe собран. Новые Unity
  defaults не приняты: лучшие по числу charts варианты не восстанавливают
  пользовательские границы. [Методика, рендеры и числа](UV_SEAM_PLACEMENT.md).

- **Свежий 5125-face input 2026-10-04 (продолжение Exp #1):** точный capture
  воспроизводит 147 baseline charts → 44 full merge → packing rejection → 96
  после прежнего halving. Поиск до шести budgets сохраняет прошедшие checkpoints:
  итог 51 charts / 16 small / 52.366% fill / zero overlaps, против 96 / 37 /
  52.726%. Существующие UV cuts вне предлагаемого edge seam теперь допускаются.
  Все final gates сохранены; 194 Unity tests passed, три off/on repeat runs
  побайтово идентичны с сохранением source corners. Обе FBX references остаются
  на 22 charts / 54.633%; до ручного качества всё ещё есть разрыв. Цена на новом
  input: ~13.8 s вместо ~5.9 s; дорогое brute-force / >512 px packing сохраняет
  прежний halving / first-success путь. Relax during growth, compactness, rotations и
  explicit-density packing проверены отдельно; не приняты как defaults.
  [Причина, сравнение layouts, отвергнутые варианты и replay](UV_MERGE_SEARCH.md).

- **Входная topology 2026-10-04 (продолжение Exp #1):** на бюсте source/raw
  voxel замкнуты; Trim удалял 4 faces и создавал 12 открытых edges, Simplify
  создавал 3 opposite-winding duplicate pairs. Trim теперь сохраняет closed
  source volume components; Simplify удаляет только безопасные collapsed fins,
  фиксирует реальные borders и проверяет edges, vertex fans и component Euler
  characteristics с retry/fallback. Результат None: 1886 faces, boundary /
  non-manifold / duplicates / degenerate = 0; 9 repeat runs в трёх режимах
  бит-идентичны. 192 Unity tests passed, skipped 0; native ABI/binaries unchanged.
  [Причина, результаты и воспроизведение](SIMPLIFY_TOPOLOGY.md).

- **Source signals 2026-10-04 (продолжение Exp #1):** curvature/cavity на high-poly
  и geometric AO перенесены через production fitted cage на оба ручных эталона
  и actual simplify. 16704 + 17940 samples, zero misses; повторные buffers
  бит-идентичны. На пяти spatial folds source повышает hard-boundary AUC
  0.844→0.903, но общий UV AUC только 0.808→0.827 / 0.801→0.830.
  После исключения hard edges преимущество для UV-разрезов исчезает:
  V1 0.530→0.505, V2 0.703→0.690. Blind source-driven default weights
  не вводились; нужны отдельно confidence и chart topology/cut planning.
  Метод, AO/curvature radii, контроль leakage и sampling limits:
  [отчёт](UV_SOURCE_SIGNALS.md), переносимый harness в `Tools~/SourceSignalsBenchmark/`.

- **Сравнение с двумя ручными эталонами 2026-10-04 (продолжение Exp #1):**
  `uvbest.fbx` содержит исправленную сетку: 1856 triangles, без duplicate faces и
  non-manifold edges. На этой же сетке прежний merge оставлял 52 islands против
  ручных 18/23. Ограничение не сводится к топологии или настройкам xatlas:
  12 проверенных charting-профилей без merge дают 72–80 islands.
  Теперь проверяются два независимых кандидата: прежняя подгонка шва (residual 2%,
  local worst 4) и более свободная (10%, local worst 6), затем прежние relax,
  полный overlap scan, repack и неизменные global gates. Свободный кандидат
  выбирается только при меньшей фрагментации и сохранении упаковки относительно
  уже проверенного строгого кандидата. На эталоне: 52 → 22 islands, fill 54.6%,
  zero overlaps. На старом 1997-face входе потеря fill 64.6% → 58.6% отвергается.
  Отмена на любом этапе сохраняет исходный объект. Во втором ручном эталоне
  обнаружено 16 positive-area intra-chart overlaps в двух charts; первый чистый.
  Числа, layouts, smoothing groups и ограничения: [отчёт](UVBEST_REFERENCE_ANALYSIS.md).

- **Packing gate 2026-10-04 после плохих пользовательских скриншотов (продолжение Exp #1):**
  zero overlap / низкий conformal stretch не гарантируют плотный атлас или равную
  texel density. UvMesh repack теперь получает charts с UV-площадью, нормализованной
  к 3D-площади; также для overlap repair без Merge. Seams приоритетны по доле
  удаляемой границы, а не chart id. Финальный gate ограничивает потерю заполнения
  5% относительно repaired baseline и chart density CV; при отказе — restart из
  snapshot с половиной merge budget. Все старые overlap/stretch/source-corner
  гарантии сохранены. На 1901 faces: старый merge 42.1% → новый 56.8%, 55 charts;
  на 1997 faces: 47.2% → 64.6%, 56 charts. Это не точный 1883-face snapshot скриншота:
  UV-only captures не содержат исходной геометрии. Теперь Info/RemeshDiag сохраняет
  полный input + настройки в `unwrap_*.bin`, последние пять файлов.
  Пределы, сравнение baseline, ограничения и реальные layouts: [отчёт](UV_PACKING_QUALITY.md).

- **Distortion relax 2026-10-04 (продолжение Exp #1):** после успешного merge
  все итоговые charts проходят guarded conformal relax перед repack.
  Свободная граница, MIPS-based energy + quadratic tail, L-BFGS (до 50 итераций),
  аналитический первый корень det-barrier и сохранение безопасных checkpoints.
  Per-chart mean/worst не увеличиваются; UV-площадь сохраняется uniform scale,
  seam/tangent дубликаты получают бит-идентичные UV; полный overlap scan обязателен.
  Source corners не меняются; ambiguous UV cuts / unsupported topology пропускаются.
  ARAP и разные места relax сравнены: all-after даёт устойчивый выигрыш без
  изменения числа shells; before меняет seam candidates и не всегда полезен.
  На bust recommended 512: 28 charts / 14 small / 0 overlap,
  mean 1.10772 → 1.05221, worst 3.75916 → 1.66454; user settings:
  36 / 19 / 0, mean 1.18992 → 1.06325, worst 3.19396 → 1.65894.
  По 10/10 production repeats побайтово идентичны; каждый source corner сохранён.
  На default 2048: 29 / 13 / 0, worst 3.90045 → 1.67871 (3 repeats).
  Relax занимает около 40 ms; pack на 2048 всё ещё дорогой (~11 s).
  Отмена после принятого merge теперь восстанавливает полный snapshot;
  диагностический лог `[UV] merge-relax` отделяет этот этап от финального pack gate.
  165/165 связанных Unity EditMode tests проходят (DirectX, без skips);
  compile-check проходит в обеих FBX define-конфигурациях.
  Метод, alternatives, corpus CSV и UV heatmap: [отчёт](UV_MERGE_RELAX_BENCHMARK.md).
- **Подбор xatlas defaults 2026-10-04 (продолжение Exp #1):** на фиксированном
  bust (1997 faces) и четырёх геометрических fixtures сравнены chart weights,
  iterations и packing: 810 phase/case/profile измерений, 1568 вызовов.
  Сбалансированный default меняет только roundness 0.01 → 0.5; cost 2,
  normal deviation 2, straightness 6, seam 4, iterations 1, rotation on,
  block alignment / brute force off остаются прежними. На bust 512/padding 2
  с Merge: 28 charts / 14 small / 0 overlap, mean 1.10772, worst 3.75916,
  451 ms; текущие сохранённые настройки пользователя: 36 / 19 / 0,
  mean 1.18992, worst 3.19396, 2615 ms (один медленный sample).
  Worst ухудшается, но остаётся ниже 4; это компромисс, не универсальный optimum.
  На 2048 без Merge новый профиль избегает дорогого overlap repair:
  890 ms против 10577 ms у прежнего default. Merge на 2048 всё ещё дорогой
  и остаётся opt-in. Сохранённые настройки не мигрируют автоматически;
  кнопка **Recommended xatlas settings** применяет только chart/packing preset.
  154/154 связанных Unity-теста проходят. Полный корпус, ограничения,
  sample counts и воспроизводимый harness: [отчёт](XATLAS_DEFAULTS_BENCHMARK.md).
- **Изменение алгоритма 2026-10-04 после реальных логов:** baseline пользователя
  уже содержал 26 внутри-chart overlap-пар на 1883 faces / 80 charts; rollback
  возвращал их без изменений. Кандидат 54 charts содержал 33 пары и 6 charts
  с разной намоткой граней. Отказ вызывала mixed winding, хотя mean/worst
  укладывались в пределы. Соседи по welded edge ошибочно исключались из overlap-теста.
  Теперь перед merge (и при выключенном merge) конфликтующие UV-грани разрезаются
  в отдельные charts: детерминированное greedy vertex cover графа конфликтов,
  дублирование вершин на границах, сохранение каждой 3D-грани и формы её UV-треугольника,
  затем repack. Результат принимается только после полной проверки нулевого overlap,
  winding и прежних stretch-пределов; незавершённая проверка не считается успехом.
  Merge сначала выравнивает намотку двух charts отражением UV при необходимости,
  затем делает прежний similarity fit. Проверка площади пересечений общая с
  диагностикой, без исключений для соседних граней. Снап охватывает все tangent/normal
  дубликаты швовой вершины; неоднозначные существующие UV-разрезы внутри chart не сливаются.
  Readback читает полные native vertex/index buffers и проверяет xref каждого
  source corner; chart splits допустимы, геометрия сохраняется. Тангенты пересчитываются
  у всех упакованных charts. Прямоугольный native atlas встраивается в квадратный UV
  с единым масштабом: независимая width/height-нормализация bridge создавала stretch
  1.43 даже на двух исходно недеформированных треугольниках.
- **Проверка на модели пользователя в Unity 6000.2.6f2:** исходный FBX
  `Meshy_AI_Distinguished_Bust_0925154648_texture`, текущие сохранённые настройки,
  отдельный локальный проект. Remesh совпадает с логом: 30288 → 51612 triangles,
  trim 4. Текущая simplification даёт 1997 faces (в прежнем логе — 1883, это другой
  snapshot настроек). Raw unwrap: 78 charts / 8 overlap-пар; repair: 82 charts /
  0 overlap; merge: 36 charts / 0 overlap (46 accepted merges). Small charts: 47 → 19;
  mean stretch: 1.17660 → 1.18992; worst: 2.90240 → 3.19396, пределы 1.29426 / 4.
  Все overlap-проверки полные, invalid/degenerate/OOB = 0; source corners сохраняются.
  10/10 unwrap-прогонов дают побайтово идентичные position/UV/index/chart buffers.
  153/153 связанных Unity EditMode-теста проходят с DirectX (UV диагностика,
  merge/repair, Remesh bake/hierarchy/normals); обе FBX define-конфигурации compile-check
  проходят. Для GPU-тестов нужен графический backend, `-nographics` даёт Null GPU.
  Это проверка Unwrap, не визуальный bake seam-review и не Playground GO-протокол.
- **Диагностика 2026-10-04:** логи `[RemeshDiag] [UV]` при уровне Info разделяют
  `merge-baseline`, `merge-candidate`, `merge-before-pack` (только внутри chart),
  `merge-after-pack (candidate)`, `merge-quality-gate`, `merge-rollback` и
  `unwrap-final mergeCharts=True/False`. Число acceptedMerges относится к кандидату,
  не к финальному результату. Gate печатает baseline/candidate, реальные mean/worst
  limits, valid и все причины отказа. Финальный атлас проверяется и с выключенным merge.
  Overlap-диагностика пересекает UV-треугольники по площади в double precision,
  не пропуская соседей с общим ребром: контакт нулевой площади разрешён, складка — нет.
  Печатаются pair counts внутри/между chart, первые 8 пар с face/chart ID (с нуля),
  сумма площадей пересечений пар (не union; тройное покрытие считается несколько раз),
  degenerate/invalid/OOB и время. Лимит 2M broad-phase сравнений ограничивает стоимость;
  `complete=False` означает только нижнюю оценку, а не доказательство отсутствия overlap.
  Отсекается площадь ≤ max(1e-16 UV², 1e-8 площади меньшего треугольника).
  Само логирование read-only; актуальные изменения алгоритма и приёмки описаны выше.
- **Replay `repack_20261004_031155.bin`:** 1883 faces, 48 charts в merge-кандидате;
  до pack 40 внутри-chart overlap-пар (pairAreaSum=0.000512322007 в UV² дампа),
  после pack 40 внутри-chart пар, 0 между-chart пар
  (pairAreaSum=0.000752826608 в UV² финального атласа). Обе проверки полные.
  Исходный атлас до merge и атлас после rollback этот дамп не содержит, поэтому
  наличие overlap в финальных 80 shell нужно проверять `merge-baseline`/`unwrap-final`.
  10 чистых диагностических тестов прошли в headless harness с math shims;
  Editor/Tests компилируются в обеих FBX define-конфигурациях. Это не Unity EditMode-прогон.

- **Вопрос:** можно ли существенно сократить xatlas-фрагментацию (число островов и мелких
  обломков) вообще без source-features и без нового parameterizer — только пост-обработкой
  выходных чартов?
- **Изменение:** переключатель **Merge charts** (off по умолчанию) в Remesh & Bake → Unwrap.
  После выбора лучшей развёртки (включая «Reduce UV fragmentation») `UvChartMerge.Apply`
  детерминированно, раундами, пробует слить соседние пары чартов: Procrustes-подобие
  (поворот + uniform scale + перенос после выравнивания winding) UV движущегося чарта по швовым вершинам,
  затем бит-точный снап шва к UV принимающего — xatlas соединяет чарты по UV-colocal,
  `faceMaterial` только разделяет, поэтому почти равный шов после repack снова распадётся на
  два острова. Гейты приёмки: residual шва ≤ 0.02 относительно диагонали UV bbox принимающего;
  scale фита в [0.5, 2] (texel density); `maxChartArea`/`maxChartBoundary` пользователя
  (boundary объединения = boundaryA + boundaryB − 2·seamLength3D); полный tri-tri overlap-тест
  (double-precision polygon clipping, разрешён только контакт нулевой площади); локальное
  растяжение после снапа на гранях движущегося чарта и швовых гранях принимающего:
  mean ≤ max(1.15, pre·1.1), worst ≤ max(4, pre·1.1), единая winding. Тангенты всех чартов
  перестраиваются по финальной раскладке атласа (Lengyel-аккумуляция по граням) — это
  покрывает и поворот merge-фита, и по-вершинное смещение снапа шва, и per-axis
  ceil-растяжение паковщика xatlas (не являющееся подобием). После снапа дополнительно
  проверяется само-перекрытие движущегося чарта (снап — не подобие и может сложить плотно
  свёрнутый чарт сам на себя). Слитые чарты перепаковываются через xatlas UvMesh bridge (`faceMaterial` = id
  чарта, ComputeCharts + PackCharts; retry с обоими rotation-флагами off при неоднозначном
  маппинге). Финальный гейт — `UvChartQuality.Measure(...).Improves(preMerge, preMerge)`;
  любая неоднозначность — откат к снапшоту. ID чартов после раундов компактируются в 0..N−1.
- **Не трогается:** native ABI и вендоренный xatlas; `originalChartCount` /
  `originalSmallChartCount` (это бейслайн до оптимизаторов); настройки чарта пользователя.
- **Протокол A/B:** сначала простая модель (куб/симметричная), затем регрессия на Playground
  LODGroup; ≥10 повторов; метрики: islands, small islands (≤8 tris), mean/max stretch, время
  стадии Unwrap; baseline — тот же unwrap с выключенным переключателем.
- **GO-критерий:** islands ↓ ≥10% на Playground-наборе при `meanStretch ≤ max(1.15, orig·1.1)`,
  `maxStretch ≤ max(4, orig·1.1)`, 10/10 байт-в-байт детерминизм UV и TransferValidator без overlaps.
- **STOP-критерий:** рост stretch за гейтами, недетерминизм, деградация бейка по швам →
  revert без компенсаций.
- **Статус:** реализация (PR Exp #1); измерения по протоколу — до мержа.
- **Найдено при ручной проверке (краш редактора):** merge-фит мог выставить UV вне [0,1]
  (повёрнутый чарт торчит за атлас), а UvMesh-пакер xatlas требует входные UV в [0,1] —
  `ASSERT texcoord.x >= 0` (xatlas.cpp:8598) и нативный краш в `xatlasPackCharts`. Фикс:
  нормализация всего лейаута одним uniform-масштабом до подачи в пакер + проверка [0,1]
  на выходе. Вторая мина рядом: `xatlasDestroy()` выполнялся до чтения выходов
  (use-after-free); сессия перестроена как create → add → pack → read → destroy.
  **Отдельный краш на фиксе:** нормализация на весь [0,1] делала суммарные extent'ы чартов
  равными атласу, per-chart ceil-округление xatlas добавляло сверху — чарты переставали
  помещаться, пакер выставлял отрицательные координаты и ассертил (тот же xatlas.cpp:8598).
  Фикс — внутренний 4× oversample (resolution × 4, padding × 4) с нормализацией выхода
  бриджем, ровно как в продакшн-repack (`XatlasRepack` internalOversample).
  **Настоящая причина тиражных крашей после нормализации (3 дампа, доказано логом+реплеем):**
  `RunNativePackAsync` рассчитан на вызов с главного потока — внутри `Task.Run(нативный пак)`
  плюс `EditorApplication.timeSinceStartup`/`UvProgress`; с worker-потока стадии Unwrap
  `timeSinceStartup` кидал исключение немедленно, `finally { xatlasDestroy(); }` освобождал
  глобальный атлас под всё ещё работающим паком на пул-потоке → use-after-free, AV-чтения
  с блуждающими адресами внутри `PackCharts`. Реплей точных входов (захват в
  `%TEMP%/meshlab-uvmerge`) нативно НЕ падал — краш был средой, не данными. Фикс: пак
  выполняется инлайн на том же worker-потоке (create → add → pack → read → destroy одним
  потоком, без editor-API и пул-тасков), с бюджетной проверкой стоимости пака.

## Эксперимент 2026-08-06 — Самодостаточная нормализация winding в repack

- **Проблема:** standalone `Repack All` и отключаемый Weld в full pipeline
  позволяли вызвать `RepackSingle`/`RepackMulti` с mirrored UV0 shell.
- **Изменение:** оба repack entry point нормализуют собственные копии UV0
  непосредственно перед передачей данных в xatlas; исходный UV0 mesh не
  изменяется.
- **Ожидание/проверка:** mirrored shell учитывается в `flippedShells`, UV2
  упаковывается в положительном winding, а тест неизменности UV0 продолжает
  проходить.

## Эксперимент 2026-08-06 — безопасные размеры oversampled atlas

- **Проблема:** умножение `resolution × internalOversample` в `uint` и расчёт
  стоимости pack в `long` могли переполниться до вызова native xatlas.
- **Изменение:** размеры вычисляются через `ulong` и отклоняются при выходе за
  диапазон `uint`; стоимость pack насыщается до `long.MaxValue`, чтобы лимит
  всегда отклонял чрезмерный atlas.
- **Ожидание/проверка:** штатные разрешения не меняются; переполняющиеся значения
  завершают repack с ошибкой до native pack.

## Эксперимент 2026-08-06 — Изоляция cross-LOD hints по mesh group

- **Проблема:** индексы source shell локальны для mesh, но overlap/match hints
  накапливались в общих списках и могли влиять на несвязанный mesh с совпавшим
  числовым индексом shell.
- **Изменение:** состояние hints разделено по паре source `MeshEntry` +
  `meshGroupKey`; между LOD передаются только hints той же пары.
- **Сохранение поведения:** внутри одной LOD-цепочки приоритет issues → hint →
  3D distance не меняется; очищение состояния перед новым transfer/auto-tune
  запуском сохранено.
- **Проверка:** требуется ручной Unity-прогон на нескольких mesh groups с
  совпадающими локальными индексами shell и сравнение UV2 на всех LOD.

## Эксперимент 2026-08-06 — Безопасная post-pack коррекция split charts

- **Проблема:** shrink исходного UV shell вокруг общего центроида сдвигал его
  разнесённые xatlas charts через занятые области атласа.
- **Изменение:** плотность и коэффициент по-прежнему считаются на исходный shell,
  но каждый фактический xatlas chart сжимается вокруг собственного центроида.
  Shell с вершинами без надёжного chart ID остаётся без изменений.
- **Ожидание:** chart после коррекции остаётся внутри собственного packed AABB;
  существующее выравнивание плотности и shrink-only поведение сохраняются.

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
- **Решение**: `SidecarStore.ClearUv2Entries()` — удаляет только UV2 entries, сохраняет collision entries.

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

---

## External MeshLab (desktop) — Settings to Avoid Artifacts

> Обновлено: 2026-05-02
>
> Применимо к FBX, прошедшим через десктопный **MeshLab** (https://www.meshlab.net/) перед импортом в Unity. Не путать с пакетом **UnityMeshLab** — это про внешний инструмент.
>
> Программная проверка симптомов: `MeshLabArtifactValidator` (`Editor/MeshLabArtifactValidator.cs`),
> menu **Tools → Mesh Lab → Validators → Check Imported MeshLab Artifacts** (по выделенному GameObject/asset).

### Filters — что ломает геометрию

1. **Merge Close Vertices** — `Distance` ставить очень маленьким (`1e-6`) или отключать filter.
   - На дефолтном threshold склеивает per-wedge UV-вершины в одну → **UV seams разъезжаются**, текстура «плывёт» по швам.
   - Симптом в Unity: `mesh.vertexCount == уникальных позиций`, при том что в сцене заметны видимые швы.

2. **Parameterization (UV1)** — выбирать filter с поддержкой существующих UV seams: `Trivial Per-Triangle Parameterization` или `Iso Parametrization`.
   - Дефолтный `Parameterization: Flat Plane` ломает развёртку UV1 (весь mesh проецируется в одну плоскость).
   - Симптом: UV1 — degenerate triangles или всё в `(0,0)`.

3. **Remove Duplicate Faces / Remove Duplicate Vertices** — отключать, если есть split UVs.
   - В 3ds Max split UVs выглядят как «дубликаты» по позиции (но имеют разные UV/normal).
   - MeshLab их склеивает → теряются UV seams и smoothing groups.

### Export FBX dialog — обязательные настройки

| Опция | Значение | Зачем |
|-------|----------|-------|
| `WedgeAllChannels` | **ON** | Сохраняет per-wedge UV / normal (т.е. split UVs / smoothing seams). |
| `UseExistingNormals` | **ON** | Не пересчитывает нормали (иначе теряются smoothing groups → flat shading). |

### Симптомы плохих настроек в Unity (что ловит валидатор)

- **No UV seams**: `vertexCount` ≈ числу уникальных позиций при наличии нескольких UV-островов → `Merge Close Vertices` или `Remove Duplicate Vertices` склеили wedge.
- **Flat-shaded normals**: > 90% треугольников имеют 3 одинаковых vertex-нормали, совпадающих с face-normal → нормали пересчитаны без сохранения smoothing.
- **Degenerate UV1**: > 5% треугольников UV1 с нулевой площадью — broken parameterization.
- **UV1 all zero**: все UV1 в `(0,0)` — MeshLab стёр UV1 канал.
- **UV1 out of unit**: > 5% UV1-вершин вне `[0..1]` — Flat Plane / Iso без нормализации.

### Проверка

Перед импортом FBX из MeshLab в Unity:
1. Прогнать `Tools → Mesh Lab → Validators → Check Imported MeshLab Artifacts` на корне.
2. Если warning — пересохранить из MeshLab с правильными настройками.
3. Если повторяется — отключить проблемный filter и переэкспортировать.

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

## Эксперимент 2026-06-03 — Stage D cascade-threshold sweep (4 кейса × 9 ячеек)

**Контекст:** Plan v2 Stage D (cascade group matching deep→fine) родил пороги `cascadeMatchFrac` × `cascadeMinHits`. Чтобы найти knee, прогнали sweep `{0.35,0.50,0.65} × {2,4,8}` на 4 моделях (Gazebo/Carousel/Playground/WoodenBox01). Артефакты — `bench_2026-06-03_00-01-29-061/{case}/hier/stage_d_sweep.csv` + `lod{N}_groups_mf*_mh*.png`.

**Что подтвердилось:**
- Каскадная идея валидна: крупные lighting-домены держат цвет (= один groupId) через все LOD'ы. WoodenBox: пол/задняя стена/правая стена/рама стабильны LOD3→LOD0. Carousel: 6 пирогов канопе и скамьи идентичны LOD2→LOD0 даже при final groupCount=922.
- `missed=0` на каждом переходе во всех кейсах. Проекция здорова, `overlayDistNorm=0.03` не зажат.
- Каскад идёт deepest-first, `reused=0` — родитель всегда резолвится до того, как finer спросит про него.

**Что НЕ работает — главные находки:**
1. **Пороги почти не влияют на качество доменов.** Визуальная сетка 3×3 на Carousel LOD0 идентична на крупных поверхностях во всех 9 ячейках. Разница 808↔1198 групп — целиком в мелких шеллах (тонкая рама, проволока, кромка). Тюнить mf/mh ради качества доменов смысла мало.
2. **Взрыв групп идёт НЕ от плохого матчинга, а от пролиферации микро-шеллов в Stage C.** Carousel LOD0 = 773 raw shells ≈ 6 крупных кусков канопе + ~767 тонких деталей. Поэтому 269→922, Playground 318→1664. И `skipAreaFrac`/`skipMaxFaceCount` тогда были объявлены в `Options.Default`, но **нигде не применялись**.
3. **`minHits` работал контр-продуктивно.** Это был доминирующий рычаг (Carousel LOD1→0 fresh при mh=2/4/8: 421/467/618). Логика «мало хитов → не доверяем голосу → fresh» наказывала именно те крошечные шеллы, которым отдельный домен нужен в последнюю очередь.
4. **`groupCount` — обманчивая метрика.** Она зависит от шума микро-шеллов, не от качества доменов. Минимизировать её = минимизировать фрагментацию проволоки.

**Изменение:** Stage D получил **tiny-shell merge ветку** (commit на бранче `claude/fix-transfer-bugs-KYVQD`):
- finer shell с `totalArea ≤ opts.skipAreaFrac × totalFineArea` И `faceCount ≤ opts.skipMaxFaceCount` force-join'ится на доминантного родителя независимо от matchedFrac/minHits, если bestProxy резолвится.
- Tiny shell без матча (`bestProxy<0` — деталь которой действительно нет на deeper LOD) всё ещё открывает fresh, но засчитывается в отдельный счётчик `tinyOrphan` для видимости. Topological-neighbour fallback для этого кейса не делаем — отложен до данных, показывающих что он нужен.
- `CascadeStat` расширен: `tinyJoined`, `tinyOrphan`. CSV свипа добавляет 2 колонки. Лог Stage D печатает их.

**НЕ сделано / открытые вопросы:**
- Scored auto-winner свипа не строим: до Stage E (final pack + per-LOD UV2 + lightmap-defect счётчики трансфера) нет объективного скаляра для оптимизации `(matchFrac, minHits, skipAreaFrac, skipMaxFaceCount)`. Свип остаётся сравнительным (PNG + CSV под глаза).
- Не нормализовали пороги по `meshDiag`: knee может смещаться между кейсами. Подтверждено в свипе для Playground (LOD3→2 join только 25% против 75% на LOD1→0) — но это геометрическая реальность (LOD3 беднее деталью), не порог.
- Default `skipAreaFrac=0.001`/`skipMaxFaceCount=4` оставлен как есть — ожидаем повторного свипа после tiny-merge, теперь уже по `tinyJoined`/`tinyOrphan`, чтобы калибровать.

**Проверка:**
- Свип-прогон 4 кейса × 9 ячеек завершился без ошибок, артефакты на месте.
- Повторный свип после tiny-merge ещё не делался — TODO следующим шагом, на тех же 4 кейсах, чтобы померить просадку `fresh` и убедиться что `tinyOrphan` мал.

## Эксперимент 2026-06-03 (продолжение) — повторный свип ПОСЛЕ tiny-merge

**Артефакты:** `bench_2026-06-03_01-02-41-045/{case}/hier/stage_d_sweep.csv` (+ per-cell PNG; это прогон до suppress-фикса). Все 4 CSV содержат заполненные `tinyJoined`/`tinyOrphan` → tiny-merge активен.

**Результат на default-ячейке (mf0.50 / mh4), fresh суммирован по переходам:**

| Кейс | seed | groupCount | fresh | tinyJoined | tinyOrphan | orph % от fresh |
|---|---|---|---|---|---|---|
| Gazebo | 138 | 222 | 84 | 59 | 61 | 73% |
| Carousel | 269 | 818 | 549 | 104 | 468 | **85%** |
| Playground | 318 | 1255 | 937 | 409 | 684 | 73% |
| WoodenBox01 | 6 | 105 | 99 | 66 | 97 | **98%** |

**Что подтвердилось:**
1. **tiny-merge делает свою работу.** `tinyJoined` поглощает штраф minHits: 59/104/409/66 шеллов, которые иначе ушли бы в fresh, теперь подсасываются к родителю. groupCount просел против pre-merge свипа (Carousel 922→818, Playground 1664→1255, Gazebo 281→222, WoodenBox 171→105).
2. **Каскад по-прежнему держит крупные домены.** Визуал `lod0_groups.png`: Carousel — 6 чистых секторов канопе + когерентные скамьи; WoodenBox — пол/стены схлопнулись в 3 цвета. `missed=0` везде.

**Что НЕ работает — решающая находка:**
3. **73–98% всех fresh-групп = `tinyOrphan`** — крошечные шеллы (area ≤ 0.001×total И faceCount ≤ 4) у которых `bestProxy < 0`, т.е. на deeper LOD геометрии под них вообще нет. tiny-merge их не трогает (force-join требует резолвящегося родителя), поэтому каждый открывает свой lighting-домен. Это и есть оставшийся источник взрыва групп — тонкий обод, трубки ножек, кромки рамы (визуально: разноцветное крошево по ободу Carousel и по рамам WoodenBox).
4. **Порог тут бессилен — это доказано данными, не на глаз.** `tinyOrphan` на финишном переходе LOD→0 имеет РОВНО ОДНО значение через все 9 ячеек: Gazebo=19, Carousel=378, Playground=83, WoodenBox=86. Он threshold-invariant by construction (`bestProxy<0`), поэтому никакой mf/mh не уберёт доминирующую часть взрыва.
5. **WoodenBox — чистейшая демонстрация:** seed всего 6 групп, финал 105, и 97 из 99 fresh — tinyOrphan. Весь его group-count это orphan-крошево на LOD0.

**Вывод / следующий шаг (ранее отложенный — теперь данные требуют его):** нужен **topological-neighbour fallback** для tinyOrphan. Раньше в коде стоял комментарий «defer until the data shows it's needed» — данные показали: на финишных LOD'ах orphan'ы дают 85–98% fresh. План: построить shell-shell adjacency на finer LOD (по общим рёбрам `canonicalTris`), и tinyOrphan вливать в соседний шелл с наибольшей общей границей, наследуя его groupId, вместо открытия fresh-группы. Открытый вопрос — что делать с orphan'ом, у которого ВСЕ соседи тоже tinyOrphan (цепочка крошечных шеллов): либо chain-resolve до первого не-tiny, либо слить такой кластер в один общий домен.

**НЕ сделано:**
- Topological fallback ещё не реализован — отложен (см. ниже: сначала Stage E даёт объективную метрику).
- Defaults `skipAreaFrac=0.001`/`skipMaxFaceCount=4` не трогали; калибровать уже по метрикам Stage E, а не по `tinyOrphan`.

## Эксперимент 2026-06-03 (Stage E) — старт пакинга домен-атласа, слайс E1

**Решение:** вместо tinyOrphan-fallback идём в Stage E. Обоснование: `groupCount`/`tinyOrphan` — это 3D-сегментация без единой UV-координаты, объективной метрики нет. Только Stage E даёт 2D-атлас → overlap/density/inverted. И вероятно orphan-крошево в атласе займёт пренебрежимо мало — решим это уже по метрикам.

**Нарезка (один концерн = один коммит):**
- **E1 (этот коммит):** `PackDomainCharts` — для каждой lighting-группы её canonical-шелл проецируется планарно (`basisU/V`, `extentU/V` из Stage C) в локальный [0,1], скармливается в `xatlasAddUvMesh` с `faceMaterial = groupId` (границы чартов по группам), `ComputeCharts`+`PackCharts` раскладывают. Выход: `r.domainAtlasRects[groupId]` (общий layout домена) + `domains_atlas.png`. Без записи мешей.
- **E2:** для каждого LOD member-шеллы проецируются в rect своей группы → `finalUv2/finalTris/finalSourceVertexIdx` → `BuildFinalMeshes` (Stage F готов) → `lodN_final_uv2.png`.
- **E3:** метрики на атласе (overlap-пары / inverted / texel density) → CSV + скаляр для пере-свипа порогов.

**Технические находки при реализации E1:**
- `xatlasAddUvMesh → ComputeCharts → PackCharts` — рабочая последовательность в этой кодовой базе (так делает `XatlasRepack.RepackSingle`); `ComputeCharts` на UV-меше НЕ пере-развёртывает, держит наши UV, а `faceMaterial` задаёт границы чартов.
- Выход `xatlasGetOutputVertexData` уже нормирован в [0,1] в этой нативной сборке — проверено по `proxy_uv2_auto.png` (читает выход напрямую, заполняет 0-1 box). Комментарий «atlas-pixel space» в `XatlasRepack.cs` устарел.

**Проверка:** Unity-компиляция в этом окружении недоступна (нет toolchain'а; тесты/FBX/бенч — ручной прогон). Сделана статическая сверка сигнатур (`xatlasAddUvMesh`/`PackCharts`/`GetOutputVertexData`), полей `Shell3D`/`Options`, баланс скобок. Прогон бенча на 4 кейсах + визуальная проверка `domains_atlas.png` — следующий шаг (ручной, в Unity).

### Результаты E1 (bench_2026-06-03_01-48-52-098, 4 кейса)

`domains_atlas.png` сгенерился во всех кейсах, Stage E отработал.

**Что ✅ работает:**
- Пакинг корректен на всех 4 кейсах: UV в [0,1], атлас плотно заполнен, без грубых меж-чартовых наложений. Data-model (groups → canonical charts → packed rects) валидирован end-to-end.
- Крупные lighting-домены = аккуратные прямоугольные чарты (Gazebo — почти идеальная сетка квадов; WoodenBox панели; Carousel секторы канопе; Playground платформы).
- Orphan-крошево, хоть его сотни (Carousel ~468), индивидуально занимает крошечную площадь — крупные домены доминируют по площади атласа. Это подтверждает, что отложить tinyOrphan-fallback было правильно: в атласе оно дешёвое.

**Что ⚠️ вскрылось — главная находка E1: фолдинг кривых шеллов.**
- Шеллы, которые **заворачиваются** (цилиндры, кольца, трубы, дуги: обод Carousel, балясины/арки Gazebo, трубы горок Playground), при планарной проекции на ОДНУ плоскость складываются сами в себя. Визуально: синусоиды (∿∿∿), плотная радужная вертикальная штриховка, «бабочки»-песочные часы, круглые розетки, C-образные завитки.
- **Корень:** `dominantNormal` = нормируемая area-weighted сумма нормалей граней (`ExtractShells`, ~813). У заворачивающегося шелла противоположные нормали взаимогасятся → `dominantNormal ≈ 0` → `ComputePlaneBasis` даёт мусорный базис → проекция вырождается/складывается. Такой чарт = внутренние наложения UV → запёкся бы мусорный лайтмап.
- **Доля невелика** и это тонкий кривой trim (по площади ≪ плоских доменов): Gazebo ~5-6 дефектных из ~280, Carousel — весь обод (заметно больше), Playground — трубы/перекладины по краю. Многие из них — те же tinyOrphan.
- **Метрика детекции (дешёвая, на этапе extraction):** `coherence = |accumNormal| / totalArea` — ≈1 для плоского шелла, →0 для заворачивающегося. Порог отделяет developable-патчи от закрученных.

## Эксперимент 2026-06-03 (Stage E2) — собственно КАСКАД: проекция LOD'ов в rect домена

**Разбор ошибки.** E1 и попытка «стичить UV0» строили Stage E как «развернуть+упаковать каждый canonical-шелл по отдельности» и спорили про параметризацию (planar vs UV0 vs фолдинг). Это **игнорировало весь смысл каскада**. Каскад — про КРОСС-LOD согласованность:
- canonical-шелл группы = самый глубокий член = тот, что **не нашёл мэтч** → **перепаковывается** (получает rect в атласе);
- **все остальные члены на всех LOD проецируются в ТОТ ЖЕ rect** (basis/centroid/extent canonical'а) → один лайтмап валиден через все LOD;
- шелл, не нашедший мэтч на своём LOD → canonical своей fresh-группы → перепакован в свой слот.

UV0/фолдинг — побочный вопрос; параметризация canonical'а вторична, главное — проекция членов в общий rect.

**Изменение (slice E2):**
- Откатил UV0 в `PackDomainCharts` — canonical снова планарный. `rotateCharts:0` ⇒ placed rect = входной [0,1] box, масштабированный/сдвинутый, значит проекцию можно воспроизвести линейным `[0,1]→rect`, и члены лягут в совпадение с canonical'ом.
- Новый `BuildCascadedUv2`: для КАЖДОГО шелла КАЖДОГО LOD проекция верт на плоскость canonical'а его группы → local [0,1] → в `domainAtlasRects[gid]`. Пишет `finalUv2/finalTris/finalSourceVertexIdx` per LOD (без дедупа, по 3 верта на грань — Stage F копирует атрибуты по `srcIdx`).
- `lod{N}_final_uv2.png` — диагностика: один домен должен занимать одну область атласа на всех LOD.

**Проверка:** Unity-компиляция недоступна (нет toolchain'а), статическая сверка типов/сигнатур/скобок. Прогон бенча + `lod{N}_final_uv2.png` (проверить совпадение области домена через LOD) — следующий ручной шаг.

**Открыто:** фолдинг кривых canonical'ов даёт искажённую (но кросс-LOD согласованную) проекцию; barycentric-pull на меше canonical'а вместо планарной плоскости — потенциальное улучшение качества, не блокер каскада.

### Результаты E2 (bench_2026-06-03_02-59-26-442) — каскад ПОДТВЕРЖДЁН ✅

`lod{N}_final_uv2.png` сгенерились на всех LOD всех кейсов. Главная проверка — кросс-LOD согласованность — пройдена.

- **WoodenBox LOD3→LOD2→LOD0:** крупные домены (пол, стены) стоят в ОДНОЙ И ТОЙ ЖЕ области атласа на всех LOD. LOD3 = ~6 чартов в конкретных rect'ах; LOD2 = те же rect'ы, дробятся на больше суб-чартов; LOD0 = те же rect'ы заняты + сотни мелких чартов трима по остатку атласа. Домен заякорен на месте, детализация растёт внутри/вокруг — by construction (шелл группы `g` на любом LOD → `domainAtlasRects[g]`), и визуал подтверждает что багов нет.
- Один лайтмап, запечённый для домена, теперь валиден через все LOD — основная цель каскада достигнута.

**Остаточный дефект (известный, не блокер):** кривые/вырожденные canonical-шеллы дают «Union Jack» чарты с белой штриховкой (внутренние наложения от планарного фолдинга). Согласованы через LOD (одинаковое искажение), но внутри чарта UV перекрываются → этот домен запечётся с артефактом. Кандидаты на фикс: barycentric-pull на меше canonical'а, либо splitting кривых шеллов на developable-патчи.

**Следующее:** либо (E3) метрики на атласе (overlap/density/inverted) для объективной оценки, либо фикс фолдинга кривых canonical'ов. Каскадный костяк готов.

## Эксперимент 2026-06-03 (Stage E fix) — убрана дисторшн-нормировка, единый тексель

**Разбор ошибки (критичной).** Я нормировал каждый шелл в свой `[0,1]` по его `extentU/extentV`. Это (а) **искажало** непрямоугольные шеллы (длинный тонкий → растянут в квадрат) и (б) **убивало тексель-density**: огромная стена и крошечный винт оба → `[0,1]`, т.е. у мелкого текселей на единицу площади в сотни раз больше. Для лайтмапа недопустимо.

**Рецепт (от пользователя): классика → выровнять UV → идентичный тексель.** Реализовано:
- **Реальные пропорции:** canonical-шелл проецируется на свою плоскость в МИРОВЫХ единицах (`inU=dot(d,basisU)`, `inV=dot(d,basisV)`), БЕЗ нормировки-в-квадрат.
- **Идентичный тексель:** xatlas пакует с ФИКСИРОВАННЫМ `texelsPerUnit` (= `atlasRes·√(packEff/totalCanonArea)`), а не auto-fit — все чарты одной плотности.
- **Выравнивание (affine):** из placed-UV canonical'а LSQ-фитом снимается аффинное `inU→atlasU, inV→atlasV` → `r.domainPlacements[gid]`. Canonical воспроизводит своё размещение, а ВСЕ члены группы на всех LOD используют ТОТ ЖЕ affine → лежат в той же области атласа с той же плотностью. Заменило `[0,1]→rect` нормировку.

**Остаётся:** «классика» пока = планарная проекция, что для ПЛОСКИХ шеллов точно (плоскость разворачивается тривиально), но кривые canonical'ы (обод/трубы) всё ещё складываются. Полный фикс — xatlas classical unwrap кривых шеллов (Stage B `fineClassicalUv2` уже считает классику per-LOD) вместо планара. Дисторшн+тексель для плоских доменов теперь корректны.

**Проверка:** Unity-компиляция недоступна, статическая сверка типов/LSQ/единиц/скобок. Прогон бенча — следующий шаг.

## Эксперимент 2026-06-03 (Stage E fix 2) — СОХРАНЯТЬ shell, не перепроецировать

**Разбор (ещё одна моя ошибка).** Даже с реальными пропорциями я всё равно ВЫВОДИЛ UV заново через `dot(d,basis)` (планарная проекция) — т.е. уничтожал исходную развёртку шелла и складывал кривые. Правильно: **сохранять shell** — взять родную UV0 шелла и **проецировать/размещать** её, ничего не перепридумывая.

**Реализация:**
- В `PackDomainCharts` вход xatlas = **сохранённая UV0** canonical-шелла, только recenter (вычесть центроид острова) + единый масштаб `S = √(area3D/areaUV0)` (форма не трогается, лишь нормируется тексель-density: UV-площадь → 3D-площадь). Фикс `texelsPerUnit` → идентичный тексель по всем доменам.
- Affine `scaled-UV0 → atlas` снимается LSQ; в `domainPlacements[gid]` теперь `{uvc, scale, su,ou,sv,ov}`.
- В `BuildCascadedUv2` каждый шелл берёт **свою UV0**, применяет placement своей группы: `in=(uv0−uvc)·scale; uv=affine(in)`. LOD'ы домена делят UV0-раскладку → одинаковый UV0 → одинаковый тексель атласа на всех LOD. Развёртка шелла сохранена, без планарной перепроекции.

Планарный `dot(d,basis)` полностью убран из Stage E. Допущение: LOD'ы делят UV0-layout одного домена (стандартная практика). Если нет — выравнивание матчей сломается, увидим на `lod{N}_final_uv2.png`.

**Проверка:** Unity-компиляция недоступна, статическая сверка типов/границ/скобок (защита от рассинхрона faceMat↔индексы и обрезанных треугольников). Прогон — следующий шаг.

### Результаты preserve-UV0 (bench_2026-06-03_08-41-51-481) — ✅ всё сошлось

Скомпилировалось (после фикса CS0136 `fc`). Картинки по 4 кейсам:
- **Фолдинг ушёл.** `domains_atlas.png`: чарты — реальные UV0-острова с настоящими пропорциями (длинные тонкие = рейки/балясины беседки, веера = секторы канопе Carousel, квадраты = панели). Синусоид/бабочек/Union-Jack больше нет.
- **Кросс-LOD согласованность держится.** WoodenBox LOD3 = 6 крупных доменов блоком 2×3 внизу-слева; LOD0 = ТЕ ЖЕ квадраты в тех же местах + тонкий трим вокруг. Один домен → одна область атласа на всех LOD.
- **Развёртка сохранена** (UV0, не перепроецирована), **тексель единый** (S-нормировка + фикс texelsPerUnit).

Итог: рецепт «сохранять shell → проецировать → выровнять → идентичный тексель» реализован и подтверждён визуально.

**Остаточные мелочи (не блокеры):** packing ~50% (packEff=0.5, можно поднять); тонкий трим — очень тонкие полоски (реальная геометрия); Carousel плотный/шумный (сотни тонких деталей). Допущение про общий UV0-layout LOD'ов подтвердилось на тест-сьюте (домены сошлись).

## Эксперимент 2026-06-11 — Stage E3 метрики + устойчивость placement'ов (E2)

**Цель:** «рабочий надёжный вариант» — у пайплайна не было объективного скаляра качества (всё по PNG на глаз) и было два тихих способа потерять геометрию/раскладку на реальных ассетах.

**Фикс 1 (E1, LSQ degenerate axis).** Канонический чарт — идеально прямая axis-aligned полоска в UV0 → нулевая дисперсия по одной оси → знаменатель per-axis LSQ ≈ 0 → placement всей группы invalid. Теперь вырожденная ось заимствует масштаб разрешённой оси (xatlas применяет uniform scale, rotateCharts:0, флипа нет); обе оси вырождены (чарт-точка) → фолбэк на расчётный `texelsPerUnit/resolution` с якорем в среднем placed-UV.

**Фикс 2 (E2, никогда не дропать грани).** Шеллы групп без валидного placement раньше молча пропускались в `BuildCascadedUv2` → дыры в финальных мешах после Apply. Теперь они эмитятся с uv2=(0,0) (плохой бейк на этих гранях, но не потерянный треугольник), считаются (`finalUnplacedFaces`) и логируются Warn'ом.

**E3 (`ComputeStageEMetrics`)** — растр финального UV2 каждого LOD на разрешении атласа (texel-centre ownership):
- `overlapTexels`/`overlapShellPairs` — тексели, на которые претендуют 2+ треугольника, не являющиеся одной гранью и не seam-adjacent внутри одного шелла (общий source-вертекс или совпадающая UV-вершина в пределах 0.75 текселя). Кросс-шелл конфликты считаются ВСЕГДА: mirror-reuse UV0 между шеллами — ровно тот дефект, который надо ловить (у зеркальных островов все вершины совпадают — нельзя вайтлистить по совпадению вершин). Прислонившиеся острова дают ~1px линии — на порядки меньше площадных настоящих overlap'ов.
- `invertedFaces`/`degenUvFaces`/`oobVerts` — перевёрнутая намотка, нулевая UV-площадь, вершины вне [0,1].
- `tpuMean/P1/P99/Spread` — area-weighted разброс texels-per-world-unit; рецепт preserve-UV0 + фикс texelsPerUnit должен держать ~1×.
- `xLodContainedPct`/`misalignedGroups` — КОНТРАКТ КАСКАДА ИЗМЕРЯЕТСЯ, А НЕ ПРЕДПОЛАГАЕТСЯ: тексели групп, чей canonical на другом LOD, должны попадать в 3×3-дилатированный футпринт canonical-LOD той же группы. Низкий containment = ассет нарушает допущение «LOD'ы делят UV0-layout» → один бейк НЕ валиден через LOD'ы. Это главный флаг надёжности на произвольных пользовательских моделях.

**Выходы:** `stage_e_metrics.csv` (строка на LOD) + `lod{N}_overlap.png` (серый — покрыто, красный — overlap) в bench-папке `hier/`; per-LOD строки в логе; диалог Apply показывает хедлайн (overlap px / unplaced faces / misaligned domains). `stage_d_sweep.csv` получил cell-level колонки `e3OverlapTexels/e3OverlapPct/e3TpuSpreadMax/e3XLodMinPct/e3MisalignedGroups/e3UnplacedFaces` — свип порогов Stage D теперь скорируемый (минимизировать e3OverlapTexels при e3XLodMinPct близком к 100).

**Известные слепые пятна метрики (задокументированы в коде):** одиночный треугольник, сложенный ровно на своего edge-соседа, проходит как adjacent (фолды глубже одного треугольника ловятся через не-смежные пары); же-шелловые острова, разорванные в UV0 на несколько кусков, не конфликтуют (они в разных местах атласа — и это корректно).

**Проверка:** Unity-компиляция в окружении недоступна; статическая сверка типов/скобок/сигнатур. Следующий ручной шаг — бенч на 4 кейсах: ожидаем `overlapPct` ≈ 0 на WoodenBox/Gazebo, заметный на Carousel если остался mirror-reuse; `tpuSpread` ~1–1.5×; `xLodContainedPct` > 85% на всём сьюте; `unplacedFaces` = 0.

## Эксперимент 2026-08-06 — Apply UV2 после source-only repack

**Проблема:** оптимизация source-only pipeline корректно пропускала transfer, но оставляла `HasTransfer = false`. Из-за общего UI-gate результат repack нельзя было применить к FBX.

**Изменение:** Quality Report и Validation Overlay по-прежнему требуют завершённого transfer, а Apply/Reset actions теперь доступны после repack или transfer. Transfer и auto-tune для source-only workflow остаются пропущенными.

**Проверка:** EditMode-тест `ApplyUv2_IsAvailable_AfterSourceOnlyRepack` фиксирует доступность Apply при `HasRepack = true` и `HasTransfer = false`.
