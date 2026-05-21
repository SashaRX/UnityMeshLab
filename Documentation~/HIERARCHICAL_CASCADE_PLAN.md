# Hierarchical Cascade Projection Plan

Architecture описанная оператором: каскад deeper→finer LOD с repack'ом
unmatched shells на каждом этапе. Документ-trail для отслеживания
прогресса. Каждый этап = отдельный коммит + бенч прогон + визуальная
проверка.

## Final goal

Каждый LOD в LODGroup получает `mesh.uv2` где:
- Shells матчащиеся с deeper LOD'ом наследуют UV proxy → пиксель в
  пиксель совпадают (shared lighting domain)
- Shells **которых нет** в deeper LOD (доп. детализация: окна, винты,
  трим) получают свой слот в атласе через xatlas repack
- Атлас растёт по мере спуска LOD3 → LOD0, итоговый атлас покрывает
  весь визуальный контент модели
- Texel density и chart size выровнены по deepest LOD

## Stage map

| # | Stage | Code change | Diagnostic to inspect | Pass criteria |
|---|---|---|---|---|
| A | Poisson coverage | `GenerateProxySamples`: убрать adaptive median filter | `<case>/hier/proxy_samples.png` | Pink dots по ВСЕМ чартам, без пустых больших rectangles | ✅ Done (`3fb4c01`) |
| B | Per-LOD classical unwrap (diag only) | Для каждого fine LOD: запустить xatlas (sym-split + ARAP + pack) с `texelsPerUnit` из deepest LOD. Сохранить как новое поле `r.fineClassicalUv2[li]`. PNG: `<case>/hier/lodN_classical_uv2.png` | Layout каждого fine LOD'а ОТДЕЛЬНО (без проекции). Сверить ориентации shells | Все shells fine LOD'а упакованы, ориентация согласна с deeper LOD'ом (визуально похоже на proxy unwrap, но плотнее) |
| C | Unmatched shell detection + viz | После Stage 5 проекции собрать fine shells где ВСЕ грани попали в sentinel UV. Подсветить на iso-views отдельной маской. PNG: `<case>/hier/lodN_unmatched.png` (iso-view, unmatched shells подсвечены красным) | Какие shells fine LOD'а остались без proxy correspondence (frame/trim/окна — фичи которых нет в deeper) | Unmatched shells сгруппированы как непрерывные регионы (а не разбросаны как noise) |
| D | Cascade pairwise projection | Refactor `BuildFinalFineUv2`: вместо "deepest=proxy для всех", сделать пары (LOD[i+1] → LOD[i]). После проекции LOD[i] становится proxy для LOD[i-1]. Per-pair Poisson + voting + UV-winding + dedup как сейчас. Каждый LOD имеет финальный `mesh.uv2` сразу как matched (matched part — нет sentinel-ов в matched zone). Unmatched ВРЕМЕННО на sentinel, разберёмся в E. | Все `lodN_final_uv2.png` — должна быть пирамидальная иерархия: LOD3 = clean unwrap, LOD2 = LOD3 + новые matched регионы, … | LOD2 содержит ВСЕ UV LOD3 plus extra coverage; LOD1 содержит ВСЕ UV LOD2 plus extra; и т.д. Размер связной UV-площади растёт от deepest к LOD0 |
| E | Repack unmatched shells into atlas | Для unmatched shells текущего LOD'а: запустить xatlas в режиме «пакуй вот эти новые charts в свободное место атласа, не трогая зафиксированные UV-rect'ы matched shells». Атлас может расти выше V=1. Repack СВЯЗАН с предыдущим proxy: matched UV не двигается. | `lodN_final_uv2.png` — НИКАКИХ sentinel UV. Полное покрытие. Unmatched регионы видно как новые charts в добавленной части атласа | На каждом fine LOD все shells имеют UV. На моделях с фичами не в deepest (окна WoodenBox, винты Carousel) — атлас V > 1 |
| F | Apply (Stage 6) compatible с каскадом | `BuildFinalMeshes` уже использует `finalUv2[li]` — но после D/E это уже не один-proxy результат, а каскадная цепочка. Возможно потребуется обновление meta (lightmapScaleOffset propagation от proxy к fine'у?) | Apply menu item: меш заменяется на клон с финальным UV. Ctrl+Z возвращает. Bake в Unity показывает корректное освещение | Bake совпадает между LOD0 и LOD3 в shared lighting domain (matched shells), новые регионы получают своё освещение |

## Per-stage test protocol

Для каждого этапа после коммита:

1. **Push** → CI должен зелёный (.meta files, compile)
2. **Bench run**: оператор запускает `Mesh Lab → Lightmap Transfer → Run Benchmark Suite` на стандартном наборе (5 моделей)
3. **Inspect**: посмотреть PNG'и указанные в таблице
4. **Verdict**:
   - ✅ Pass criteria выполнен → следующий stage
   - ❌ Артефакт → описать что вижу → стоп до фикса

Никаких "продолжаю в следующий стейдж пока этот сломан".

## Current state

- ✅ Stage A — Poisson coverage fix (commit `3fb4c01`)
- ⬜ Stage B — Per-LOD classical unwrap (next)
- ⬜ Stage C — Unmatched detection
- ⬜ Stage D — Cascade pairwise projection
- ⬜ Stage E — Repack unmatched into atlas
- ⬜ Stage F — Apply compat + Bake validation

## Non-goals (chosen explicitly)

- НЕ хочу runtime-pipeline — это editor tool
- НЕ хочу skinned mesh support (deferred)
- НЕ хочу автоматически менять `lightmapScaleOffset`/`lightmapIndex` пост-бэйка (отдельный feature)
- НЕ хочу ray-cast fallback в Stage 5 — sentinel честный сигнал о коридорах Poisson coverage
- НЕ хочу clamp / scale-to-fit shell'ов под чарт — distortion запрещён архитектурно
- НЕ хочу собственного хитрого UV-распаковщика — xatlas даёт всё что нужно

## Open questions для оператора

Перед запуском stage B:
1. Атлас в каскаде растёт вверх (V > 1) ИЛИ всегда нормализуется в [0,1]? Я бы сделал growth вверх — даёт ясный сигнал сколько детали добавлено finer LOD'ом. Можно нормализовать в конце если нужно.
2. На stage B нужен ли отдельный visualization PNG per LOD, или достаточно записать в Result поле для последующих stage'ов? Я склоняюсь к PNG для контроля + поле для cascade'а.
3. Если на stage D/E атлас в одном fine LOD'е растёт больше чем в другом — это OK или надо унифицировать? Я бы делал per-LOD атлас (LOD0 может быть выше чем LOD2), Unity сам справится при бэйке.

Если все три по моему — поехали в B без дальнейших вопросов.
