# Hierarchical Cascade Projection Plan (v2 — group-then-final-pack)

Architecture (оператор, уточнённая): каскад deeper→finer LOD
**устанавливает ГРУППЫ (lighting domains)**, НЕ UV-позиции. Во время
каскада шелы разных LOD'ов группируются по transfer-correspondence.
Рабочий атлас может «расти» по мере добавления новых групп, но это
скретч. **Финальная паковка — ОДНА, в самом конце**, когда всё
смэтчилось, с нашими параметрами (texel density, padding, blockAlign).

Этот сдвиг (паковка отложена в конец) растворяет две дыры старого
плана:
- «xatlas пакует в свободное место не трогая зафиксированное» —
  больше не нужно: финал это обычный fresh-pack всех групп, что xatlas
  и делает идеально.
- «каскадный proxy = накопленный uv2» — больше не проблема: каскад
  несёт group-labels, не UV. Membership пропагируется транзитивно.

## Data model

```
LightingDomainGroup {
    int groupId;
    int canonicalLod;      // самый ГЛУБОКИЙ LOD имеющий шел в группе
    int canonicalShellId;  // shell id внутри canonicalLod
    List<(int lod, int shellId)> members;  // все LOD-шелы одного домена
}
```
Плюс per-LOD: `shellId → groupId`.

Канонический шел (deepest member) — владелец параметризации домена.
Его classical-unwrap локальный UV определяет форму чарта. Все finer
члены проецируются НА его геометрию (ortho + barycentric) и наследуют
его placed UV.

## Final goal

Каждый LOD получает `mesh.uv2` где:
- Шелы в одной группе → один lighting domain → один чарт атласа,
  пиксель-в-пиксель между LOD'ами (через проекцию finer→canonical)
- Шелы которых нет в deeper (окна/винты/трим) → новая группа → свой
  чарт
- Texel density единый (один `texelsPerUnit` на финальном паке)
- Атлас вырастает чтобы вместить ВСЕ группы

## Stage map (v2)

| # | Stage | Что делает | Diagnostic | Pass criteria | Status |
|---|---|---|---|---|---|
| A | Poisson coverage | `GenerateProxySamples`: убран adaptive median filter | `proxy_samples.png` | Pink dots по ВСЕМ чартам | ✅ `3fb4c01` |
| B | Per-LOD classical unwrap | xatlas (sym-split+ARAP+pack) на каждый non-deepest LOD. Diagnostic + источник shell-форм для финал-пака. **Texel НЕ выравниваем тут — это забота финал-пака.** | `lodN_classical_uv2.png` | Каждый LOD пакуется чисто, без инверсий | ✅ `3bcfdfb` |
| —  | **Legacy purge** | Удалён весь PR-2 single-proxy classifier + PR-3 single-proxy projector. -1042 строки. Build() остался только: Stage 1 (proxy unwrap variants) + Stage B (per-LOD classical) + Stage 2 (Poisson) + Stage 3 (sample→fineLOD). Apply menu graceful no-op до Stage E. | — | Brace balance 0, CI зелёный, бенч работает (без `final_uv2.png`) | ✅ `c9948e6` |
| **C** | **Per-LOD 3D shell extract + group seed** ⬅ **СЕЙЧАС** | См. ниже | См. ниже | См. ниже | ⬜ next |
| D | Cascade grouping (deep→fine) | Цикл li = deepest-1 … 0. На каждом шаге: Poisson на LOD[li+1] (immediate deeper), project на LOD[li], per-shell vote за deeper-shell → group. matched-fraction ≥ порог → join группы; иначе → новая группа. **Только membership, НЕ UV.** | `lodN_groups.png` (iso, faces по groupId — ОДИН цвет across LODs = один домен) | Соответствующие шелы разных LOD'ов = один цвет. Unmatched = новые цвета. Никакого noise-разброса | ⬜ |
| E | Final pack + per-LOD uv2 | 1) xatlas pack всех canonical-чартов (геометрия canonical членов) ОДНИМ вызовом, unified texelsPerUnit+padding+blockAlign. 2) Per group: построить target (worldVerts + placed uv2 canonical члена). 3) Finer члены: ortho-project на canonical target → barycentric → uv. 4) Seam-dup по groupId. Записать `finalUv2[li]`. | `lodN_final_uv2.png` (ВСЕ LOD в ОДНОМ атласе), `atlas.png` | Все LOD в общем атласе. Группа = один чарт. Никаких sentinel. Атлас покрывает весь контент | ⬜ |
| F | Apply + Bake | `BuildFinalMeshes`+`Apply` (готовы) на каскадный результат. Bake в Unity. | Apply menu, Ctrl+Z, bake | Bake совпадает между LOD0..LOD3 в shared domain | ⬜ |

## ⬅ Stage C: что именно сейчас делаем

**Цель:** На каждом LOD'е независимо извлечь 3D шелы и создать
data-model для каскада. **Никакого матчинга / UV / проекции — только
сегментация + seed.**

### Code changes

1. **Структура `LightingDomainGroup`** (новая):
   ```csharp
   public struct LightingDomainGroup
   {
       public int groupId;
       public int canonicalLod;        // самый глубокий LOD-член
       public int canonicalShellId;    // shell id внутри canonicalLod
       public List<(int lod, int shellId)> members;
   }
   ```

2. **Новые поля Result:**
   ```csharp
   // 3D шелы на каждом LOD (индекс = LOD level)
   public Shell3D[][] perLodShells;
   // shellId per face per LOD (faces[lod][faceIdx] = shellId или -1 для degen)
   public int[][] perLodFaceToShell;
   // Группы — на старте только seed от deepest
   public LightingDomainGroup[] groups;
   // shellId → groupId per LOD; начальное состояние: deepest заполнен,
   // остальные LOD'ы = все -1 (Stage D присвоит)
   public int[][] perLodShellToGroup;
   ```

3. **Новый метод `ExtractPerLodShellsAndSeedGroups(LODGroup, Options, Result)`** —
   вызывается из `Build()` после Stage 3:
   - Цикл по всем LOD'ам:
     - `BuildFaceData` + `ExtractShells` с теми же параметрами что
       раньше использовал deepest (`opts.shellNormalThresholdDeg`,
       `opts.shellMergeAngleDeg`)
     - Сохранить `perLodShells[li]` и `perLodFaceToShell[li]`
   - Seed groups: для каждой shell на deepest LOD'е → новая
     `LightingDomainGroup{ canonicalLod=deepest, canonicalShellId=si,
     members=[(deepest, si)] }`
   - `perLodShellToGroup[deepest]` = 0..N-1
   - `perLodShellToGroup[li]` = все -1 для не-deepest

4. **Диагностический PNG `WritePerLodShellsPngs`** — iso view каждого
   LOD'а раскрашенный по shellId. Используем тот же isometric проектор
   что Stage 3's `WriteProxyHitsPngs` (методы `IsoProject`,
   `RasterizeTrianglePx` живы). Палитра — простая hash-based hue.

5. **Wiring:** добавить вызов после Stage 3 в `Build()`, добавить
   `WritePerLodShellsPngs` в `BuildAndWriteForCase`.

### Diagnostic output

Per case: `lod0_shells.png`, `lod1_shells.png`, … `lod{deepest}_shells.png`

Каждый PNG — iso view меша, грани окрашены по shellId. На deepest LOD
ожидаем картинку похожую на `proxy_uv2_active.png` по структуре (тот
же набор больших регионов). На finer LODs ожидаем БОЛЬШЕ шелов
(больше детализации) либо ТО ЖЕ количество (если LOD-меши одинаковые
кроме triangulation).

### Pass criteria (как тестируем)

1. **Прогон бенча** на 5 моделях, смотрим новые `lodN_shells.png` per
   case.
2. **Каждый LOD имеет вменяемое число шелов:** не 1 гигантский (все
   слились) и не 1 на каждый tri (всё фрагментировано). Сверяем
   глазами с известной геометрией модели.
3. **Шелы — непрерывные регионы:** один цвет = один связный кусок
   меша. Никакой пятнистости / разброса noise.
4. **Deepest LOD's shells ≈ proxy chart structure:** сравнить
   `lod{deepest}_shells.png` против `proxy_uv2_active.png` — должны
   видеть тот же набор больших регионов (cymsplit может разбить
   некоторые шелы в proxy на 2 чарта; это OK).
5. **Console log:** счётчик `[Stage C] LOD{li}: {N} shells, seeded
   {M} groups (canonical=deepest)` для каждой модели.

### Что Stage C **НЕ ДЕЛАЕТ**

- Не матчит шелы между LOD'ами (это Stage D)
- Не строит UV2 (это Stage E)
- Не апдейтит Apply menu (он остаётся no-op до Stage E)
- Не трогает `finalUv2/Tris/SourceVertexIdx` поля Result

### После прохождения Stage C

Идём в **Stage D** — каскадный grouping deep→fine. На каждом шаге:
1. Берём текущий «proxy LOD» (на первой итерации = deepest)
2. Poisson-сэмплим его геометрию (`GenerateProxySamples` уже есть, но
   надо его параметризовать — сейчас он сэмплит `proxyUv2` фиксы; в
   Stage D возможно надо геометрический Poisson, без UV-зависимости —
   решим при реализации D)
3. `ProjectProxySamplesOntoFineLods` (есть) — раздаёт sample-hits на
   per-face buckets fine-LOD'а
4. Для каждой fine-shell: tally proxy-shell votes, matched-fraction ≥
   порог → join родительскую группу; иначе → новая группа с
   `canonicalLod=fine_LOD_id`
5. Diagnostic `lodN_groups.png` — ВЫРОВНЕННАЯ палитра across LODs:
   одна группа = один цвет на всех LOD'ах где она присутствует. Это
   визуально подтверждает что cascade сошёлся.

---

## Per-stage test protocol

1. **Push** → CI зелёный (.meta files, compile)
2. **Bench run** на 5 моделях
3. **Inspect** PNG из таблицы
4. **Verdict:**
   - ✅ pass → next | ❌ артефакт → описать что вижу → стоп до фикса

Никаких «продолжаю в следующий стейдж пока этот сломан».

## Current state

- ✅ A — Poisson coverage (`3fb4c01`)
- ✅ B — Per-LOD classical unwrap (`3bcfdfb`)
- ✅ Legacy purge (`c9948e6`) — -1042 строки
- ⬜ **C — Per-LOD 3D shell extract + group seed** ⬅ **СЕЙЧАС**
- ⬜ D — Cascade grouping
- ⬜ E — Final pack + per-LOD uv2
- ⬜ F — Apply + bake

## Resolved gaps (vs v1)

- **Дыра 1 texel:** решается на финал-паке (один texelsPerUnit на все группы). Stage B classical остаётся auto (диагностика).
- **Дыра 2 index spaces:** канон = original-mesh face space. Classical (b-space) даёт только формы canonical-чартов для финал-пака. Per-LOD uv2 строится один раз в конце в original face space, seam-dup в самом конце.
- **Дыра 3 locked-charts:** растворена — финал это fresh-pack всех групп.
- **Дыра 4 accumulated proxy:** растворена — каскад несёт group-labels, membership транзитивна (LOD1 шел → LOD2 группа → корень LOD3).
- **Дыра 5 Poisson per level:** Stage D Poisson'ит LOD[li+1] на каждом шаге цикла.
- **Дыра 6 matched threshold:** Stage D — per-shell matched-fraction порог (старт 0.5, крутим по бенчам). < порог → новая группа.

## Non-goals

- НЕ runtime — editor tool
- НЕ skinned mesh (deferred)
- НЕ автоменять lightmapScaleOffset/lightmapIndex пост-бэйка
- НЕ ray-cast fallback в проекции — sentinel честный сигнал Poisson-coverage
- НЕ clamp/scale-to-fit шелов — distortion запрещён
- xatlas = per-shell параметризация + ФИНАЛЬНЫЙ pack всех групп; промежуточного «pack into free space» НЕТ (растворено)
