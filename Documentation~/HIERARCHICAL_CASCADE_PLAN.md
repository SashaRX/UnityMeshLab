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

| # | Stage | Что делает | Diagnostic | Pass criteria |
|---|---|---|---|---|
| A | Poisson coverage | `GenerateProxySamples`: убран adaptive median filter | `proxy_samples.png` | Pink dots по ВСЕМ чартам | ✅ `3fb4c01` |
| B | Per-LOD classical unwrap | xatlas (sym-split+ARAP+pack) на каждый non-deepest LOD. Diagnostic + источник shell-форм для финал-пака. **Texel НЕ выравниваем тут — это забота финал-пака.** | `lodN_classical_uv2.png` | Каждый LOD пакуется чисто, без инверсий | ✅ `3bcfdfb` |
| C | Per-LOD 3D shell extract + group seed | Извлечь 3D шелы на КАЖДОМ LOD (`ExtractShells`, как для deepest). Создать data model. Seed: каждый шел deepest LOD → своя группа (canonical=deepest). | `lodN_shells.png` (iso, faces раскрашены по shellId) | На каждом LOD шелы выделены как непрерывные регионы, число вменяемое | ⬜ |
| D | Cascade grouping (deep→fine) | Цикл li = deepest-1 … 0. На каждом шаге: Poisson на LOD[li+1] (immediate deeper), project на LOD[li], per-shell vote за deeper-shell → group. matched-fraction ≥ порог → join группы; иначе → новая группа. **Только membership, НЕ UV.** | `lodN_groups.png` (iso, faces по groupId — ОДИН цвет across LODs = один домен) | Соответствующие шелы разных LOD'ов = один цвет. Unmatched = новые цвета. Никакого noise-разброса | ⬜ |
| E | Final pack + per-LOD uv2 | 1) xatlas pack всех canonical-чартов (геометрия canonical членов) ОДНИМ вызовом, unified texelsPerUnit+padding+blockAlign. 2) Per group: построить target (worldVerts + placed uv2 canonical члена). 3) Finer члены: ortho-project на canonical target → barycentric → uv. 4) Seam-dup по groupId. Записать `finalUv2[li]`. | `lodN_final_uv2.png` (ВСЕ LOD в ОДНОМ атласе), `atlas.png` | Все LOD в общем атласе. Группа = один чарт. Никаких sentinel. Атлас покрывает весь контент | ⬜ |
| F | Apply + Bake | `BuildFinalMeshes`+`Apply` (готовы) на каскадный результат. Bake в Unity. | Apply menu, Ctrl+Z, bake | Bake совпадает между LOD0..LOD3 в shared domain | ⬜ |

## Resolved gaps (vs v1)

- **Дыра 1 texel:** решается на финал-паке (один texelsPerUnit на все группы). Stage B classical остаётся auto (диагностика).
- **Дыра 2 index spaces:** канон = original-mesh face space. Classical (b-space) даёт только формы canonical-чартов для финал-пака. Per-LOD uv2 строится один раз в конце в original face space, seam-dup в самом конце.
- **Дыра 3 locked-charts:** растворена — финал это fresh-pack всех групп.
- **Дыра 4 accumulated proxy:** растворена — каскад несёт group-labels, membership транзитивна (LOD1 шел → LOD2 группа → корень LOD3).
- **Дыра 5 Poisson per level:** Stage D Poisson'ит LOD[li+1] на каждом шаге цикла.
- **Дыра 6 matched threshold:** Stage D — per-shell matched-fraction порог (старт 0.5, крутим по бенчам). < порог → новая группа.

## Per-stage test protocol

1. Push → CI зелёный (.meta, compile)
2. Bench run на 5 моделях
3. Inspect PNG из таблицы
4. ✅ pass → next | ❌ артефакт → описать → стоп до фикса

## Current state

- ✅ A — Poisson coverage (`3fb4c01`)
- ✅ B — Per-LOD classical unwrap (`3bcfdfb`)
- ⬜ C — 3D shell extract + group seed
- ⬜ D — Cascade grouping
- ⬜ E — Final pack + per-LOD uv2
- ⬜ F — Apply + bake

## Non-goals

- НЕ runtime — editor tool
- НЕ skinned mesh (deferred)
- НЕ автоменять lightmapScaleOffset/lightmapIndex пост-бэйка
- НЕ ray-cast fallback в проекции — sentinel честный сигнал Poisson-coverage
- НЕ clamp/scale-to-fit шелов — distortion запрещён
- xatlas = per-shell параметризация + ФИНАЛЬНЫЙ pack всех групп; промежуточного «pack into free space» НЕТ (растворено)
