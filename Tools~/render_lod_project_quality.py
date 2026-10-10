"""Render measured project-asset LOD comparison sheets from Unity GPU captures.

Run LodProjectVisualQualityTests with -meshlabLodProjectCases <dataset.json>
and -meshlabLodVisualOutput <directory>, then pass that directory here.
"""
import argparse
import json
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image
sys.dont_write_bytecode = True
from render_lod_visual_quality import raw, interior


def difference(directory, fixture, variant, view, mode):
    def read(name, channel):
        return raw(directory / f"{fixture}-{name}-{view}-{channel}.rgba")
    visible = interior((read("source", "coverage")[..., 0] > .5) &
                       (read(variant, "coverage")[..., 0] > .5))
    channels = 4 if mode == "color" else 3
    delta = np.max(np.abs(read("source", mode)[..., :channels] - read(variant, mode)[..., :channels]), axis=2).copy()
    delta[~visible] = np.nan
    return np.flipud(delta)


def budget_variants(lookup):
    return ["source"] + sorted((v for v in lookup if v.startswith("budget-lod")),
                               key=lambda v: int(v[len("budget-lod"):]))


def budget_levels(lookup):
    return " · ".join(f"LOD{v[len('budget-lod'):]} {lookup[v]['targetRatio']:.1%}"
                      for v in budget_variants(lookup)[1:])


def sheet(directory, report, view, compact=False):
    fixture = report["fixture"]
    lookup = {c["variant"]: c for c in report["captures"] if c["view"] == view}
    variants = list(lookup)
    budget_run = "budget-lod1" in lookup or "corrected-lod1" in lookup or "qslim-lod1" in lookup or "hard-lod1" in lookup
    quality_run = "quality-lod1" in lookup
    correction_run = "corrected-lod1" in lookup
    qslim_run = "qslim-lod1" in lookup
    hard_run = "hard-lod1" in lookup
    if compact:
        variants = ["source", "triangles-lod2", "unchecked-lod2"] if report["varyingVertexColors"] else ["source", "triangles-lod1", "triangles-lod2"]
        if "triangles-high-lod2" in lookup:
            variants.insert(2,"triangles-high-lod2")
        if "far-relaxed-lod2" in lookup:
            variants = ["source","triangles-lod2","far-relaxed-lod2"]
            if report["varyingVertexColors"]:
                variants.append("unchecked-lod2")
        part_variants = [v for v in lookup if v.startswith("parts-lod")]
        if part_variants:
            part_variant = part_variants[-1]
            variants = ["source",part_variant.replace("parts-","far-relaxed-"),part_variant]
        if budget_run:
            variants = budget_variants(lookup)
        if quality_run:
            variants = ["source","budget-lod1","quality-lod1","budget-lod2","quality-lod2"]
        if correction_run:
            variants = ["source","quality-lod1","corrected-lod1","quality-lod2","corrected-lod2"]
        if qslim_run:
            variants = ["source","meshopt-lod1","qslim-lod1","meshopt-lod2","qslim-lod2"]
        if hard_run:
            variants = ["source","unprotected-lod1","hard-lod1","unprotected-lod2","hard-lod2"]
    textured = (directory / f"{fixture}-source-{view}-textured.png").exists()
    colored = report["varyingVertexColors"]
    modes = ["textured" if textured else "shaded", "wire", "color" if colored else "shaded"]
    labels = ["Исходная текстура" if textured else "Форма / нормали", "Сетка", "Настоящий vertex color" if colored else "Освещение", "Ошибка RGBA" if colored else "Ошибка освещения"]
    error_mode = "color" if colored else "shaded"
    fig, axes = plt.subplots(4, len(variants), figsize=(4.1 * len(variants), 13), squeeze=False)
    fig.subplots_adjust(left=.07, right=.97, top=.84, bottom=.20, hspace=.055, wspace=.025)
    cmap = matplotlib.colormaps["inferno"].copy()
    cmap.set_bad("#080c12")
    for col, variant in enumerate(variants):
        capture = lookup[variant]
        for row, mode in enumerate(modes):
            axes[row, col].imshow(Image.open(directory / f"{fixture}-{variant}-{view}-{mode}.png"))
        error_image = axes[3, col].imshow(difference(directory, fixture, variant, view, error_mode), cmap=cmap, vmin=0, vmax=.1)
        for row in range(4):
            axes[row, col].set_xticks([])
            axes[row, col].set_yticks([])
            for spine in axes[row, col].spines.values():
                spine.set_visible(False)
        title = {"source": "Исходник · LOD0", "triangles-lod1": "Triangles · LOD1", "triangles-lod2": "Triangles · LOD2", "far-relaxed-lod1": "Строгий профиль · LOD1", "far-relaxed-lod2": "Мягкий профиль · LOD2", "parts-lod2": "LOD2 · мелкие части", "far-relaxed-lod4": "Мягкий профиль · LOD4", "parts-lod4": "LOD4 · мелкие части", "triangles-high-lod1": "Triangles High · LOD1", "triangles-high-lod2": "Triangles High · LOD2", "loops-high-lod1": "Loops High · LOD1", "loops-high-lod2": "Loops High · LOD2", "unchecked-lod2": "LOD2 · без проверки RGBA"}.get(variant, variant)
        if variant.startswith("budget-lod"):
            title = "Fast · LOD" + variant[-1] if quality_run else "Бюджет ×3 · LOD" + variant[-1]
        if variant.startswith("quality-lod"):
            title = ("До коррекции · LOD" if correction_run else "Balanced · LOD") + variant[-1]
        if variant.startswith("corrected-lod"):
            title = "После коррекции · LOD" + variant[-1]
        if variant.startswith("meshopt-lod"):
            title = "Meshoptimizer · LOD" + variant[-1]
        if variant.startswith("unprotected-lod"):
            title = "Без защиты рёбер · LOD" + variant[-1]
        if variant.startswith("hard-lod"):
            title = "Жёсткие рёбра сохранены · LOD" + variant[-1]
        if variant.startswith("qslim-lod"):
            title = "libigl QSlim · LOD" + variant[-1]
            if (capture.get('reductionNote') or '').startswith('QSLIM INPUT REFUSED'):
                title += "\nвход отклонён — показан исходник"
        budget = f"\nцель {capture['targetTriangles']}" if variant != "source" else ""
        if capture.get("allowedColorError",0) > 0 and variant != "source":
            budget += f" · RGBA {'ориентир' if budget_run else '≤'} {capture['allowedColorError']:.2f}"
        if variant.startswith("parts-"):
            budget += f"\nудалено {capture.get('removedParts',0)} частей / {capture.get('removedPartTris',0)} исходных tris"
        unchanged = "\nисходник сохранён" if variant != "source" and capture["triangles"] == report["sourceTriangles"] else ""
        axes[0, col].set_title(f"{title}\n{capture['triangles']:,} треугольников{budget}{unchanged}", fontsize=10)
        maximum = capture["maxPixelRgbaError" if colored else "maxShadingError"]
        rms = capture["rmsPixelRgbaError" if colored else "rmsShadingError"]
        axes[3, col].set_xlabel(f"max {maximum:.4f} · RMS {rms:.4f}\nсилуэт Δ {capture['silhouetteMismatch']:.2%}", fontsize=10)
    for row, label in enumerate(labels):
        axes[row, 0].set_ylabel(label, fontsize=11)
    colorbar_axis = fig.add_axes([.27, .105, .50, .012])
    bar = fig.colorbar(error_image, cax=colorbar_axis, orientation="horizontal")
    bar.set_label("Линейная ошибка 0 … 0.1; значения выше 0.1 насыщены", fontsize=10)
    colors = "неоднородный RGBA" if colored else "RGBA постоянный" if report["hasVertexColors"] else "vertex color отсутствует"
    levels = "LOD4 · цель 6.25% от исходника" if "parts-lod4" in lookup else "LOD1 50%, LOD2 25% от исходника"
    if budget_run:
        levels = budget_levels(lookup) + " от исходника; ошибки не блокируют бюджет"
    if quality_run:
        levels += " · Fast против выбора из 3 вариантов"
    if correction_run:
        levels = "LOD1 33.3%, LOD2 11.1% · одинаковая геометрия до / после коррекции нормалей и RGBA"
    if qslim_run:
        levels = "LOD1 ≈33.3%, LOD2 ≈11.1% · QSlim целится в фактический бюджет meshoptimizer · оба после коррекции"
    if hard_run:
        levels = "Цели LOD1 33.3%, LOD2 11.1% · жёсткие рёбра могут ограничить достижение бюджета"
    fig.suptitle(f"fps-project · {fixture} · {report['meshName']}\n"
                 f"Реальный FBX · {report['unity']} / {report['graphicsApi']} · {colors}\n"
                 f"{'Спереди' if view == 'front' else 'Под углом'} · {levels}", fontsize=14, y=.97)
    fig.text(.5, .025, "Диагностический shader: исходное albedo + headlight, без оригинального URP-материала / normal map.\n"
             "Карта ошибки — общий видимый участок, край 2 px исключён; силуэт измерен отдельно.\n"
             "Нулевые ошибки при неизменном числе треугольников означают сохранение исходника, а не успешное упрощение.", ha="center", fontsize=10)
    destination = directory / f"{fixture}-{'compact' if compact else view}-review.png"
    fig.savefig(destination, dpi=125)
    plt.close(fig)
    return destination


def overview(directory, reports):
    reports = [r for r in reports if r["captures"]]
    budget_run = any(c["variant"].startswith("budget-") for r in reports for c in r["captures"])
    quality_run = any(c["variant"].startswith("quality-") for r in reports for c in r["captures"])
    correction_run = any(c["variant"].startswith("corrected-") for r in reports for c in r["captures"])
    qslim_run = any(c["variant"].startswith("qslim-") for r in reports for c in r["captures"])
    hard_run = any(c["variant"].startswith("hard-") for r in reports for c in r["captures"])
    group_columns = max(len(budget_variants({c["variant"]: c for c in r["captures"]})) for r in reports) if budget_run else 3
    if quality_run:
        group_columns = 3
    rows = (len(reports)+1)//2
    fig, axes = plt.subplots(rows,group_columns*2,figsize=(group_columns*6,4.3*rows),squeeze=False)
    fig.subplots_adjust(left=.025,right=.985,top=.90,bottom=.055,hspace=.40,wspace=.02)
    for axis in axes.flat:
        axis.axis("off")
    for n, report in enumerate(reports):
        row, group = divmod(n,2)
        fixture = report["fixture"]
        mode = "textured" if (directory/f"{fixture}-source-oblique-textured.png").exists() else "color" if report["varyingVertexColors"] else "shaded"
        lookup = {c["variant"]:c for c in report["captures"] if c["view"]=="oblique"}
        variants = ["source","triangles-lod2","far-relaxed-lod2"] if "far-relaxed-lod2" in lookup else ["source","triangles-lod1","triangles-lod2"]
        part_variants = [v for v in lookup if v.startswith("parts-lod")]
        if part_variants:
            part_variant = part_variants[-1]
            variants = ["source",part_variant.replace("parts-","far-relaxed-"),part_variant]
        if budget_run:
            variants = budget_variants(lookup)
        if quality_run:
            variants = ["source","budget-lod2","quality-lod2"]
        if correction_run:
            variants = ["source","quality-lod2","corrected-lod2"]
        if qslim_run:
            variants = ["source","meshopt-lod2","qslim-lod2"]
        if hard_run:
            variants = ["source","unprotected-lod2","hard-lod2"]
        for k, variant in enumerate(variants):
            axis = axes[row,group*group_columns+k]
            axis.imshow(Image.open(directory/f"{fixture}-{variant}-oblique-{mode}.png"))
            capture = lookup[variant]
            retained = " · сохранён исходник" if k and capture["triangles"]==report["sourceTriangles"] else ""
            label = {"source":"LOD0", "triangles-lod1":"LOD1", "triangles-lod2":"LOD2 строгий", "far-relaxed-lod2":"LOD2 мягкий", "parts-lod2":"LOD2 мелкие части", "far-relaxed-lod4":"LOD4 мягкий", "parts-lod4":"LOD4 мелкие части"}.get(variant, variant)
            if variant.startswith("budget-lod"):
                label = "LOD"+variant[-1]+(" Fast" if quality_run else "")
            if variant.startswith("quality-lod"):
                label = "LOD"+variant[-1]+(" до коррекции" if correction_run else " Balanced")
            if variant.startswith("corrected-lod"):
                label = "LOD"+variant[-1]+" после коррекции"
            if variant.startswith("meshopt-lod"):
                label = "LOD"+variant[-1]+" Meshoptimizer"
            if variant.startswith("unprotected-lod"):
                label = "LOD"+variant[-1]+" без защиты рёбер"
            if variant.startswith("hard-lod"):
                label = "LOD"+variant[-1]+" рёбра сохранены"
            if variant.startswith("qslim-lod"):
                label = "LOD"+variant[-1]+" QSlim"
                if (capture.get('reductionNote') or '').startswith('QSLIM INPUT REFUSED'):
                    label += " · ВХОД ОТКЛОНЁН"
            axis.set_title(f"{fixture} · {label}\n{capture['triangles']:,} tris{retained}",fontsize=10)
    comparison = "исходник / строгий LOD2 / мягкий LOD2" if any(c["variant"] == "far-relaxed-lod2" for r in reports for c in r["captures"]) else "исходник / LOD1 50% / LOD2 25%"
    if any(c["variant"] == "parts-lod2" for r in reports for c in r["captures"]):
        comparison = "исходник / LOD2 / LOD2 с анализом мелких частей"
    if any(c["variant"] == "parts-lod4" for r in reports for c in r["captures"]):
        comparison = "исходник / LOD4 / LOD4 с удалением мелких частей"
    if budget_run:
        comparison = "LOD0 / " + budget_levels({c["variant"]: c for c in reports[0]["captures"]}).replace(" · "," / ")
    if quality_run:
        comparison = "LOD0 / LOD2 Fast / LOD2 Balanced · цель 11.1%"
    if correction_run:
        comparison = "LOD0 / LOD2 до коррекции / LOD2 после коррекции · 11.1%, одинаковая геометрия"
    if qslim_run:
        comparison = "LOD0 / LOD2 meshoptimizer / LOD2 libigl QSlim · ≈11.1%, оба после переноса и коррекции"
    if hard_run:
        comparison = "LOD0 / LOD2 без защиты / LOD2 с жёсткими рёбрами · цель 11.1%, фактические числа указаны"
    fig.suptitle(f"Реальные модели fps-project · {comparison}\n"
                 "Фактические GPU-кадры Unity; авторский vertex color, исходные текстуры при наличии",fontsize=18,y=.98)
    fig.text(.5,.015,"Диагностическое освещение; оригинальные URP-материалы и normal maps не воспроизведены. Полные сетки и карты ошибок — в отдельных листах.",ha="center",fontsize=11)
    fig.savefig(directory/"project-overview.png",dpi=125)
    plt.close(fig)


def correction_chart(directory, reports):
    measured = [(report, capture) for report in reports for capture in report['captures']
                if capture['view'] == 'front' and capture['variant'].startswith('corrected-')]
    if not measured:
        return
    fig, axes = plt.subplots(1, 2, figsize=(16, 10), gridspec_kw={'width_ratios': [1.4, 1]})
    fig.subplots_adjust(left=.18, right=.98, top=.86, bottom=.13, wspace=.55)
    normal_labels = [f"{r['fixture']} · LOD{c['variant'][-1]}" for r, c in measured]
    y = np.arange(len(measured))
    axes[0].barh(y-.18, [c['normalRmsBefore'] for _, c in measured], height=.34, color='#818a99', label='До коррекции')
    axes[0].barh(y+.18, [c['normalRms'] for _, c in measured], height=.34, color='#2a9d8f', label='После проверки')
    axes[0].set_yticks(y, normal_labels)
    axes[0].invert_yaxis()
    axes[0].set_xlabel('Средняя угловая ошибка по поверхности, градусы')
    axes[0].set_title('Нормали · сравнение с LOD0')
    axes[0].legend(loc='upper right')
    colored = [(r, c) for r, c in measured if r['varyingVertexColors']]
    y = np.arange(len(colored))
    axes[1].barh(y-.18, [max(c['colorRmsBefore'].values()) for _, c in colored], height=.34, color='#818a99')
    axes[1].barh(y+.18, [max(c['surfaceColorRms'].values()) for _, c in colored], height=.34, color='#e9a23b')
    axes[1].set_yticks(y, [f"{r['fixture']} · LOD{c['variant'][-1]}" for r, c in colored])
    axes[1].invert_yaxis()
    axes[1].set_xlabel('Максимальный RMS среди каналов RGBA')
    axes[1].set_title('Авторский vertex color · сравнение с LOD0')
    for axis in axes:
        axis.grid(axis='x', alpha=.2)
        axis.set_axisbelow(True)
        axis.spines[['top', 'right']].set_visible(False)
    fig.suptitle('Коррекция атрибутов на реальных FBX из fps-project\nLOD1 ≈ 1/3, LOD2 ≈ 1/9 · до / после — те же вершины, треугольники и UV', fontsize=17, y=.96)
    fig.text(.5, .035, 'Меньше — лучше. Ошибки измерены по поверхности в обе стороны относительно LOD0.\n'
             'При ухудшении RMS или максимума канал сохранён. Альфа проверяется отдельно.\n'
             'Это не оценка исходных URP-материалов; отдельные GPU-кадры могут ухудшиться.', ha='center', fontsize=11)
    fig.savefig(directory/'attribute-errors.png', dpi=150)
    plt.close(fig)


def qslim_chart(directory, reports):
    rows = []
    for report in reports:
        lookup = {c['variant']: c for c in report['captures'] if c['view'] == 'front'}
        if 'qslim-lod2' not in lookup:
            continue
        valid = not (lookup['qslim-lod2'].get('reductionNote') or '').startswith('QSLIM INPUT REFUSED')
        rows.append((report['fixture'], lookup['meshopt-lod2'], lookup['qslim-lod2'], report, valid))
    if not rows:
        return
    fig, axes = plt.subplots(1, 2, figsize=(16, 8))
    fig.subplots_adjust(left=.15, right=.98, top=.82, bottom=.18, wspace=.45)
    labels = [r[0] + ('' if r[4] else ' · QSlim отклонён') for r in rows]
    for axis, field, title, factor in [(axes[0], 'sourceDistanceRms', 'Ошибка формы · RMS / диагональ исходника, %', 100),
                                        (axes[1], 'silhouetteMismatch', 'Ошибка силуэта · худший GPU-вид, %', 100)]:
        def value(row, backend):
            if backend == 'qslim' and not row[4]:
                return float('nan')
            if field == 'silhouetteMismatch':
                return max(c[field] for c in row[3]['captures'] if c['variant'] == backend+'-lod2')*factor
            return row[1 if backend == 'meshopt' else 2][field]*factor
        y = np.arange(len(rows))
        axis.barh(y-.18, [value(r, 'meshopt') for r in rows], height=.34, color='#818a99', label='Meshoptimizer')
        axis.barh(y+.18, [value(r, 'qslim') for r in rows], height=.34, color='#2a9d8f', label='libigl QSlim')
        axis.set_yticks(y, labels); axis.invert_yaxis(); axis.set_title(title)
        axis.grid(axis='x', alpha=.2); axis.set_axisbelow(True); axis.spines[['top','right']].set_visible(False)
    axes[0].legend(loc='upper right')
    fig.suptitle('Сравнение LOD2 на восьми реальных FBX из fps-project\nQSlim получает фактические цели meshoptimizer; оба результата после проверки атрибутов', fontsize=17, y=.96)
    misses = [f"{r[0]}: {r[2]['triangles']} вместо {r[2]['targetTriangles']}" for r in rows if r[4] and not r[2]['budgetReached']]
    refused = [r[0] for r in rows if not r[4]]
    status = ('Не достигнуты цели: '+ '; '.join(misses)) if misses else 'Все действительные результаты достигли цели.'
    if refused:
        status += ' Отклонены: '+', '.join(refused)
    fig.text(.5, .035, 'Меньше — лучше. QSlim использует 3D QEM без стоимости UV / нормалей / RGBA.\n'
             +status+'\n'
             'Это диагностические виды, без исходных URP-материалов и normal maps.', ha='center', fontsize=11)
    fig.savefig(directory/'qslim-errors.png', dpi=150); plt.close(fig)


def smoothing_comparison(directory, reports, baseline):
    previous = {r['fixture']: r for p in baseline.glob('*-metrics.json')
                if (r := json.loads(p.read_text(encoding='utf-8'))).get('sourcePath')}
    rows = []
    for report in reports:
        before = previous[report['fixture']]
        if before['sourceSha256'] != report['sourceSha256']:
            raise ValueError('Smoothing comparison source changed: '+report['fixture'])
        old = {c['variant']: c for c in before['captures'] if c['view'] == 'front'}
        for current in report['captures']:
            if current['view'] != 'front' or not current['variant'].startswith(('meshopt-', 'qslim-')):
                continue
            prior = old[current['variant']]
            if prior['triangles'] != current['triangles']:
                raise ValueError('Smoothing comparison triangle budget changed: '+report['fixture'])
            rows.append(dict(fixture=report['fixture'], variant=current['variant'], triangles=current['triangles'],
                             normalBefore=prior['normalRms'], normalAfter=current['normalRms'],
                             shadingBefore=prior['rmsShadingError'], shadingAfter=current['rmsShadingError']))
    (directory/'smoothing-before-after.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
    fig, axes = plt.subplots(1, 2, figsize=(15, 7))
    for axis, variant, title in zip(axes, ('meshopt-lod2', 'qslim-lod2'), ('Meshoptimizer', 'libigl QSlim')):
        selected = [r for r in rows if r['variant'] == variant]
        y = np.arange(len(selected))
        axis.barh(y-.18, [r['normalBefore'] for r in selected], height=.34, color='#818b9b', label='Прежняя коррекция')
        axis.barh(y+.18, [r['normalAfter'] for r in selected], height=.34, color='#2a9d8f', label='Области сглаживания')
        axis.set_yticks(y, [r['fixture'] for r in selected]); axis.invert_yaxis()
        axis.set_title(title); axis.set_xlabel('Угловая RMS ошибка нормалей к LOD0, градусы')
        axis.grid(axis='x', alpha=.2); axis.set_axisbelow(True); axis.legend()
    fig.suptitle('LOD2: коррекция нормалей на тех же восьми реальных FBX', fontsize=16)
    fig.text(.5, .015, 'Меньше — лучше. Проверки каждой области и связь UV-дубликатов могут отклонить прежнее глобальное улучшение.\n'
             'Это ошибка поля нормалей, а не оценка исходных URP-материалов или normal maps.', ha='center', fontsize=11)
    fig.tight_layout(rect=[0, .075, 1, .94]); fig.savefig(directory/'smoothing-errors.png', dpi=150); plt.close(fig)
    return rows


def hard_edge_chart(directory, reports):
    rows = []
    for report in reports:
        lookup = {c['variant']: c for c in report['captures'] if c['view'] == 'front'}
        if 'hard-lod2' in lookup:
            rows.append((report, lookup['unprotected-lod2'], lookup['hard-lod2']))
    if not rows:
        return
    fig, axes = plt.subplots(1, 3, figsize=(19, 8))
    y = np.arange(len(rows)); labels = [r['fixture'] for r, _, _ in rows]
    for axis, field, title, factor in zip(axes, ('triangles', 'missingHardEdges', 'normalRms'),
                                         ('Фактические треугольники', 'Потерянные исходные жёсткие сегменты', 'RMS нормалей к LOD0, градусы'), (1, 1, 1)):
        axis.barh(y-.18, [b[field]*factor for _, b, _ in rows], height=.34, color='#818a99', label='Без защиты')
        axis.barh(y+.18, [a[field]*factor for _, _, a in rows], height=.34, color='#2a9d8f', label='С защитой')
        if field == 'triangles':
            axis.scatter([a['targetTriangles'] for _, _, a in rows], y, marker='|', s=180, color='#c24d36', label='Цель 1/9')
        axis.set_yticks(y, labels); axis.invert_yaxis(); axis.set_title(title)
        axis.grid(axis='x', alpha=.2); axis.set_axisbelow(True)
    axes[0].legend()
    fig.suptitle('LOD2: строгая защита жёстких рёбер на восьми реальных FBX', fontsize=17)
    fig.text(.5, .025, 'Меньше — лучше. Проверены положения рёбер, нормали с обеих сторон, защищённые грани и стыки патчей.\n'
             'С защитой бюджет может быть превышен; это сравнение одинаковых запрошенных целей, а не одинаковой сложности.', ha='center', fontsize=11)
    fig.tight_layout(rect=[0, .09, 1, .94]); fig.savefig(directory/'hard-edge-errors.png', dpi=150); plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument('--smoothing-baseline', type=Path)
    args = parser.parse_args()
    directory = args.directory.resolve()
    reports = [report for path in sorted(directory.glob("*-metrics.json"))
               if (report := json.loads(path.read_text(encoding="utf-8"))).get("sourcePath")]
    settings_path = directory / "run-settings.json"
    settings = json.loads(settings_path.read_text(encoding="utf-8-sig")) if settings_path.exists() else {}
    for report in reports:
        if not report.get("partPixelLimit") and settings.get("partPixelLimit"):
            report["partPixelLimit"] = settings["partPixelLimit"]  # Recorded run inputs, not inferred measurements.
    overview(directory,reports)
    correction_chart(directory,reports)
    qslim_chart(directory,reports)
    hard_edge_chart(directory,reports)
    smoothing_rows = smoothing_comparison(directory,reports,args.smoothing_baseline.resolve()) if args.smoothing_baseline else []
    lines = ["# Real project LOD GPU evaluation", "", "These are imported FBX models copied from fps-project; geometry, normals and vertex colors are not procedural fixtures.", "",
             "Generation calls the same LodPipelineOps.Generate path as LOD Gen. LOD1 and LOD2 independently target 50% and 25% of LOD0. "
             "Target Error=0.2, UV2 Weight=20, Normal Weight=1, Color Weight=1, Max Color Error=0.02, Lock Border=false. "
             "Full Loops uses five candidates where original FBX topology can be loaded. Missing topology is reported, never inferred from triangles. "
             "Triangle Fast uses one color-weight variant; Triangle High uses up to five. Failed color checks also trigger up to seven safer budget probes. "
             "Each native simplification starts from LOD0; a previously validated generated mesh may be reused as a separately owned candidate, never simplified recursively.", "",
             "far-relaxed uses the same strict LOD1 settings, then LOD2 Target Error=0.3, Normal Weight=0.5, Color Weight=0.25 and Max Color Error=0.10. "
             "This permits bounded RGBA loss at the last level; it does not disable validation or remove the color channel.", "",
             "The unchecked-lod2 control disables the final RGBA rejection, retaining native color costs. It is a comparison showing why a result may be rejected, not an accepted protected LOD.", "",
             "A single largest non-collision, non-lower-LOD mesh is selected per FBX. Images use one original albedo texture when supplied, "
             "with a diagnostic headlight shader. They do not reproduce the project's URP materials, packed material maps or normal maps. "
             "The report is observational: capture test success does not mean all budgets or visual quality limits passed.", ""]
    if any(c["variant"].startswith("budget-") for r in reports for c in r["captures"]):
        requested_levels = budget_levels({c["variant"]: c for c in reports[0]["captures"]})
        lines = lines[:4] + [
            f"This budget-priority run targets independent LOD0 fractions: {requested_levels}. "
            "Native edge-collapse retries relax attribute costs, error tolerance and attribute seam protection only when the requested budget is missed. "
            "All source attribute channels and material slots are retained. Lock Border is off. "
            "Final geometry, normal and RGBA errors are measured against LOD0; color/normal limits are reported without rejecting a reduced mesh. "
            "Caps and Target Error in the table are requested profile values; retries may relax native costs and error bounds, as recorded in generation notes.", "",
            "Captures use two orthographic views at 384 pixels with diagnostic headlight shading and original albedo where supplied. "
            "They do not reproduce original URP materials or normal maps. Budget success does not establish visual quality.", ""]
    correction_run = any(c["variant"].startswith("corrected-") for r in reports for c in r["captures"])
    if correction_run:
        lines = lines[:4] + [
            "This run compares Balanced (3 geometry candidates) before and after surface attribute correction. "
            "LOD1 targets 1/3 and LOD2 1/9 of LOD0 independently; positions, indices, UVs and attribute formats are asserted identical before/after. "
            "Near/far requested profiles remain Target Error 0.2/0.3, Normal Weight 1/0.5, Color Weight 1/0.25, maximum RGBA 0.02/0.1; budget takes priority. "
            "The correction fits barycentrically interpolated normal XYZ and linear RGBA against same-material LOD0 correspondence in both directions. "
            "Nine equal-area training centroids per face and sparse regularized least squares are used, with three blend trials. "
            "Authored normal RMS must improve without increasing authored maximum angle. Each RGBA channel's measured RMS and maximum must not increase, with at least one RMS improving. "
            "Rejected changes retain the original channel. Color32 is verified after quantization; float/HDR fields retain their format and local observed range. "
            "Duplicated hard normal/color seam vertices and vertices shared by material slots are pinned. Tangents are orthogonalized to accepted normals with handedness preserved.", "",
            "These gates are sampled surface checks, not proofs of raster improvement, semantic mask preservation or original URP/normal-map appearance. "
            "GPU captures use diagnostic headlight shading and original albedo where supplied, in two 384-pixel orthographic views. "
            "CPU normal RMS uses distinct quadrature from fitting, but this is not an independent holdout dataset.", ""]
    qslim_run = any(c['variant'].startswith('qslim-') for r in reports for c in r['captures'])
    if qslim_run:
        lines = lines[:4] + [
            "This comparison uses libigl Python bindings 2.6.3 in an isolated experiment directory, without changing distributed Unity native plugins. "
            "The baseline is regenerated with the current Balanced (3 strategies) budget-first meshoptimizer path and accepted normal/RGBA correction. "
            "Each QSlim LOD starts from the exact same source positions and original FBX control-point connectivity, separately per material slot, targeting the baseline's actual triangle count. "
            "Input edge/vertex manifoldness, winding, degeneracy and control-point weld distance are checked. Refused inputs show a clearly marked source reference, not a substitute simplifier. "
            "An explicit --remove-degenerate-faces preparation removes only source faces below the recorded zero-area tolerance, remapping birth-face provenance to the unchanged original triangles. "
            "QSlim uses 3D geometry quadrics; attribute costs are absent and intersection blocking is false. "
            "UV/normal/RGBA corner charts are reconstructed from birth-face provenance, then geometry/winding-based source projection transfers attributes within those charts. "
            "When no facing-compatible point exists in the birth chart, unrestricted nearest projection stays inside that chart; the corner count is recorded. "
            "The same verified normal/RGBA fitting is attempted afterward. Formats, all UV channel dimensions, source hashes and exact exported-array hashes are retained/verified. "
            "Native output count, rather than the Python binding's return status, determines budget attainment: this binding omits the native boolean success flag.", "",
            "Both LOD levels and two 384-pixel GPU views use the same source transforms, shaders and optional original albedo. "
            "Geometry distance for both backends uses unrestricted same-material nearest points in both directions, separately from attribute-facing correspondence. RMS uses four equal-area centroids; maxima additionally sample vertices/edge midpoints. "
            "This compares two complete experimental pipelines: 3D QSlim + chart transfer versus attribute-weighted meshoptimizer + retained attributes, both with optional accepted fitting. "
            "It does not establish equivalence of attribute costs, normal-map appearance, original URP material parity or production-ready QSlim integration.", "",
            "## LOD2 matched-budget comparison", "",
            "| Model | Meshopt / QSlim actual tris | QSlim matched target | QSlim status | Source distance RMS meshopt / QSlim | Normal RMS meshopt / QSlim | Worst GPU silhouette meshopt / QSlim | Worst GPU shading RMS meshopt / QSlim | RGBA max-channel RMS meshopt / QSlim |",
            "| --- | ---: | ---: | --- | --- | --- | --- | --- | --- |"]
        for report in reports:
            before = [c for c in report['captures'] if c['variant'] == 'meshopt-lod2']
            after = [c for c in report['captures'] if c['variant'] == 'qslim-lod2']
            if not before or not after:
                continue
            b, a = before[0], after[0]
            valid = not (a.get('reductionNote') or '').startswith('QSLIM INPUT REFUSED')
            if not valid:
                lines.append(f"| {report['fixture']} | {b['triangles']} / refused | {a['targetTriangles']} | INPUT REFUSED | N/A | N/A | N/A | N/A | N/A |")
                continue
            lines.append(f"| {report['fixture']} | {b['triangles']} / {a['triangles']} | {a['targetTriangles']} | {'reached' if a['budgetReached'] else 'missed'} | "
                         f"{b['sourceDistanceRms']:.3%} / {a['sourceDistanceRms']:.3%} | {b['normalRms']:.2f} / {a['normalRms']:.2f} | "
                         f"{max(c['silhouetteMismatch'] for c in before):.3%} / {max(c['silhouetteMismatch'] for c in after):.3%} | "
                         f"{max(c['rmsShadingError'] for c in before):.5f} / {max(c['rmsShadingError'] for c in after):.5f} | "
                         f"{max(b['surfaceColorRms'].values()):.5f} / {max(a['surfaceColorRms'].values()):.5f} |")
        lines.append('')
        if smoothing_rows:
            lines.extend(['### Previous versus region-aware LOD2 correction', '',
                'Source hashes and actual triangle counts match the supplied preceding comparison. '
                'The previous correction used only a global normal gate and independently fitted UV duplicates. '
                'Stricter regional and shared-variable constraints can retain a higher global RMS while protecting local continuity. '
                'The shader error here is the front diagnostic GPU view; full tables below retain both views.', '',
                '| Model | Backend | Normal RMS previous / current, degrees | Front GPU shading RMS previous / current |',
                '| --- | --- | ---: | ---: |'])
            for row in smoothing_rows:
                if row['variant'].endswith('lod2'):
                    lines.append(f"| {row['fixture']} | {row['variant']} | {row['normalBefore']:.2f} / {row['normalAfter']:.2f} | "
                                 f"{row['shadingBefore']:.5f} / {row['shadingAfter']:.5f} |")
            lines.extend(['', '![Normal RMS comparison](smoothing-errors.png)', ''])
        topology_path = directory / 'qslim-topology.json'
        if topology_path.exists():
            topology = json.loads(topology_path.read_text(encoding='utf-8'))
            lines.extend([
                '## Independent QSlim connectivity diagnostics', '',
                'These measurements use native geometric connectivity before render-vertex attribute splitting, separately per material slot. '
                'C is the number of edge-connected face components, chi is Euler characteristic and B is the number of closed boundary loops. '
                'Each slot must preserve these values independently; sums below only shorten the display. '
                'Both output levels are checked for edge/vertex manifoldness, winding conflicts and zero-area faces. '
                'These checks do not certify absence of intersections or preservation of authored quad loops. '
                'Intersection blocking was disabled in this run. No corresponding meshoptimizer connectivity claim is made.', '',
                '| Model | Raw source C / chi / B | Prepared source C / chi / B | QSlim LOD2 C / chi / B | Both levels preserve prepared invariants and validity |',
                '| --- | --- | --- | --- | --- |'])
            def invariant_text(slots):
                values = [sum(s[key] for s in slots) if all(s[key] is not None for s in slots) else None
                          for key in ('components', 'euler', 'boundaryLoops')]
                return ' / '.join('undefined' if v is None else str(v) for v in values)
            for item in topology:
                source = item['source']
                levels = item['levels']
                final = next((level for level in levels if level['level'] == 2), None)
                preserved = len(levels) == 2 and all(
                    level['valid'] and len(level['submeshes']) == len(source) and all(
                        all(out[key] == original[key] for key in ('components', 'euler', 'boundaryLoops')) and
                        out['edgeManifold'] and out['vertexManifold'] and
                        out['windingConflicts'] == 0 and out['zeroAreaFaces'] == 0
                        for original, out in zip(source, level['submeshes']))
                    for level in levels)
                final_text = invariant_text(final['submeshes']) if final and final['valid'] else 'refused'
                lines.append(f"| {item['fixture']} | {invariant_text(item['rawSource'])} | {invariant_text(source)} | {final_text} | {'yes' if preserved else 'no'} |")
            lines.append('')
            for item in topology:
                removed = sum(s['faces'] for s in item['rawSource']) - sum(s['faces'] for s in item['source'])
                if removed:
                    lines.append(f"{item['fixture']}: preparation removed {removed} zero-area source faces. "
                                 'Its invariants are compared against the prepared input; the table retains the raw-source values separately.')
            lines.extend([
                'The original FBX is unchanged and birth-face indices still address its original triangle list.', ''])
    if any(c['variant'].startswith('hard-') for r in reports for c in r['captures']):
        lines = lines[:4] + [
            'This run regenerates the same eight FBX meshes with Balanced three-candidate meshoptimizer, with and without strict hard-edge protection. '
            'LOD1 targets 1/3 and LOD2 1/9 independently from LOD0, with the same near/far costs and normal/RGBA correction. '
            'Exact positional edge adjacency and discontinuous authored endpoint normals identify creases; UV/color seams alone do not. '
            'Both incident triangles are retained unchanged in geometry; remaining triangles form patches simplified with Lock Border, even during permissive zero-cost budget probes. '
            'Ambiguous adjacency (non-manifold edges, winding conflicts, coincident opposite faces) is retained conservatively and reported separately. '
            'Required crease edges, authored normals on both sides, original protected triangles and exact patch interfaces are checked again after attribute correction. '
            'A failed check retains the source and reports a fallback. No per-vertex native ABI or distributed plugin changes are used. '
            'If retained face count already reaches/exceeds the requested target, aggressive error/weight retries are disabled; '
            'the three strategies retain their respective costs and requested error bound, ranking over-budget candidates by measured quality. '
            'For eligible decreasing protected sequences, candidates cannot increase preceding density; if every new strategy is denser, '
            'a preceding source-derived mesh is cloned, remeasured and corrected without recursive collapse. This additional candidate is reported separately. '
            'Requested normal/RGBA bounds remain diagnostics in budget-priority output; verified correction rejects regressions from its own baseline, '
            'but does not establish that these absolute requested bounds were reached. '
            'This deliberately conservative belt can make either requested budget unreachable; actual counts and missed targets are shown, without claiming matched complexity. '
            'These are working-mesh authored normal boundaries, not original DCC smoothing-group IDs; exact coincident geometry cannot establish original control-point identity.', '',
            'Two actual 384-pixel Unity GPU views use diagnostic headlight shading and optional original albedo. '
            'Original URP materials, normal maps, gameplay distances and transition popping are not reproduced.', '',
            '## Hard feature and budget measurements', '',
            '| Model | LOD | Unprotected / protected / target tris | Source hard edges | Lost unprotected / protected edges | Protected faces | Missing faces / interfaces | Protected budget | Source fallback | Normal RMS before / after |',
            '| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: |']
        for report in reports:
            lookup = {c['variant']: c for c in report['captures'] if c['view'] == 'front'}
            for level in (1, 2):
                b, a = lookup[f'unprotected-lod{level}'], lookup[f'hard-lod{level}']
                lines.append(f"| {report['fixture']} | {level} | {b['triangles']} / {a['triangles']} / {a['targetTriangles']} | "
                             f"{a['hardEdges']} | {b['missingHardEdges']} / {a['missingHardEdges']} | {a['protectedTriangles']} | "
                             f"{a['missingProtectedTriangles']} / {a['missingPatchInterfaces']} | {'reached' if a['budgetReached'] else 'missed'} | "
                             f"{a['hardEdgeSourceFallback']} | {b['normalRms']:.2f} / {a['normalRms']:.2f} |")
        lines.extend(['', '![Hard-edge comparison](hard-edge-errors.png)', ''])
        if settings.get('captureLineage'):
            lines.extend(['Run provenance: '+settings['captureLineage'], '',
                          'Verification: '+settings.get('finalVerification', 'See the retained Unity XML results.'), ''])
    if any(c.get('smoothingRegions', 0) for r in reports for c in r['captures']):
        lines.extend(['## Smoothing-region normal correction', '',
            'Regions are inferred from connected LOD0 edges with continuous endpoint normals; they are not original DCC smoothing-group IDs. '
            'UV duplicates are linked only through unambiguous smooth edge adjacency within the same assigned source region. '
            'Normal fitting shares one variable across these copies and uses region-restricted source projection. '
            'Mixed/unmapped faces, vertices shared by different regions and incomplete correspondences are pinned. '
            'Accepted normal trials must improve global angular RMS without worsening the global maximum, and must not increase any measured region RMS or maximum (0.0001 degree tolerance). '
            'Missing regions and mixed faces expose correspondence loss; these diagnostics do not restore removed hard edges or certify all crease preservation. '
            'Full before/after region errors and unresolved sample counts remain in JSON.', '',
            '| Model | Variant | Source regions | Linked normal duplicates | Mixed / unmapped faces pinned | Source regions without a mapped face | Incomplete regions pinned | Normal fit accepted |',
            '| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |'])
        for report in reports:
            for c in report['captures']:
                if c['view'] != 'front' or not c.get('smoothingRegions', 0):
                    continue
                lines.append(f"| {report['fixture']} | {c['variant']} | {c['smoothingRegions']} | {c['linkedNormalDuplicates']} | "
                             f"{c['mixedSmoothingFaces']} | {c['missingSmoothingRegions']} | {c['incompleteSmoothingRegions']} | {'yes' if c['normalsCorrected'] else 'kept'} |")
        lines.append('')
    if any(c["variant"] == "parts-lod4" for r in reports for c in r["captures"]):
        lines.extend(["This run overrides the two-level example above: four independent LOD0 targets are 50%, 25%, 12.5% and 6.25%; only LOD4 is captured. Far settings interpolate from LOD2 toward the same LOD4 endpoint. "
                      "The 12 px part-size experiment is an explicit run input (see run-settings.json), rather than the tool's 2 px default.", ""])
    if any(c["variant"].startswith("quality-") for r in reports for c in r["captures"]):
        lines.extend(["Fast keeps the first budget-reaching native result. Balanced compares three strategies from LOD0: balanced costs, shape emphasis and normal emphasis. "
                      "High additionally offers RGBA and UV emphasis. All strategies use the same requested triangle target; extra triangles cannot win on quality. "
                      "A result undershooting the target by more than max(2 triangles, 5%) loses to a result in that budget band. "
                      "The relative selection score sums normalized area RMS distance (1% source diagonal), CPU silhouette mean (5%) and maximum (20%), "
                      "normal RMS / max(15°, requested angle) weighted by Normal Weight, maximum RGBA RMS / max(0.02, requested color bound) weighted by Color Weight, "
                      "and protected UV RMS / 0.1 weighted by UV Weight. CPU silhouettes are six double-sided 128-pixel source-local orthographic views. "
                      "This is an explicit ranking heuristic, not an absolute quality grade or gameplay acceptance guarantee. The independent GPU captures below can reveal tradeoffs.", ""])
    if correction_run:
        lines.extend(["## Attribute correction measurements", "",
                      "| Model | LOD | Tris | Normals accepted | Normal RMS before / after | Authored normal max before / after | RGBA accepted | RGBA RMS before / after | GPU shading RMS before / after (worst view) |",
                      "| --- | --- | ---: | --- | --- | --- | --- | --- | --- |"])
        for report in reports:
            for level in (1,2):
                before = [c for c in report['captures'] if c['variant'] == f'quality-lod{level}']
                after = [c for c in report['captures'] if c['variant'] == f'corrected-lod{level}']
                if not before or not after:
                    continue
                c = after[0]
                rgba = lambda v: ', '.join(f'{v[ch]:.5f}' for ch in 'xyzw')
                lines.append(f"| {report['fixture']} | {level} | {c['triangles']} | {c['normalsCorrected']} | "
                             f"{c['normalRmsBefore']:.3f} / {c['normalRms']:.3f} | {c['authoredNormalMaxBefore']:.3f} / {c['authoredNormalMaxAfter']:.3f} | "
                             f"{c['colorsCorrected']} | {rgba(c['colorRmsBefore'])} / {rgba(c['surfaceColorRms'])} | "
                             f"{max(b['rmsShadingError'] for b in before):.5f} / {max(a['rmsShadingError'] for a in after):.5f} |")
        lines.append("")
    for report in reports:
        if not report["captures"]:
            lines.extend([f"## {report['fixture']}", "", "Generation/capture did not complete.", ""])
            continue
        for view in ("front", "oblique"):
            sheet(directory, report, view)
        sheet(directory, report, "oblique", compact=True)
        lines.extend([f"## {report['fixture']}", "", f"Source: `{report['sourcePath']}`", "", f"Mesh: `{report['meshName']}`; {report['sourceTriangles']} triangles, {report['sourceVertices']} vertices, {report['submeshes']} material slots.", "",
                      f"Vertex color: {'varying' if report['varyingVertexColors'] else 'constant' if report['hasVertexColors'] else 'absent'}. "
                      f"Full Loops: {report['loopStatus']}; quads loaded: {report['sourceQuads']}.", "",
                      "| View | Variant | Tris / target | Color limit | Target Error | Max RGBA | RMS RGBA | Max shading | RMS shading | Silhouette Δ | RGBA max surface | Source distance | Normal error |",
                      "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: |"])
        for c in report["captures"]:
            field_max = f"{c['maxPixelRgbaError']:.5f}" if report["hasVertexColors"] else "N/A"
            field_rms = f"{c['rmsPixelRgbaError']:.5f}" if report["hasVertexColors"] else "N/A"
            surface = ", ".join(f"{c['surfaceColorMax'][v]:.5f}" for v in "xyzw") if report["hasVertexColors"] else "N/A"
            limit = f"{c['allowedColorError']:.3f}" if c.get("allowedColorError",0) > 0 else "off / N/A"
            target_error = f"{c['targetError']:.3f}" if c.get("targetError",0) > 0 else "N/A"
            source_distance = f"{c['sourceDistance']:.3%}" if "sourceDistance" in c else "N/A"
            normal_error = f"{c['normalError']:.2f}°" if "normalError" in c else "N/A"
            lines.append(f"| {c['view']} | {c['variant']} | {c['triangles']} / {c['targetTriangles']} | {limit} | {target_error} | {field_max} | {field_rms} | "
                         f"{c['maxShadingError']:.5f} | {c['rmsShadingError']:.5f} | {c['silhouetteMismatch']:.3%} | {surface} | {source_distance} | {normal_error} |")
        notes = sorted({c.get("reductionNote") for c in report["captures"] if c.get("reductionNote")})
        for capture in report["captures"]:
            candidates = capture.get("budgetCandidates")
            if not candidates or capture["view"] != "front" or not capture["variant"].startswith(("quality-","meshopt-","unprotected-","hard-")):
                continue
            lines.extend(["", f"### {capture['variant']} candidates", "",
                          "| Variant | Selected | Tris | Native probes | Relative score | Distance RMS | Normal RMS | RGBA RMS max channel | UV RMS | CPU silhouette mean / max |",
                          "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |"])
            for candidate in candidates:
                chosen = "yes" if candidate["variant"] == capture["selectedCandidate"] else ""
                lines.append(f"| {candidate['name']} | {chosen} | {candidate['triangles']} | {candidate['nativeProbes']} | {candidate['score']:.4f} | "
                             f"{candidate['distanceRms']:.3%} | {candidate['normalRms']:.2f}° | {candidate['colorRms']:.5f} | {candidate['uvRms']:.5f} | "
                             f"{candidate['silhouetteMean']:.2%} / {candidate['silhouetteMax']:.2%} |")
        if report.get("parts"):
            lines.extend(["", f"Disconnected components: {len(report['parts'])}. Connectivity: {report['partConnectivity']}.", "",
                          "Estimated size uses 1080 px screen height and entry heights 0.25 (LOD2) / 0.0625 (LOD4). It is a conservative bounds estimate, not measured visibility.", "",
                          "| Part | Source tris | Area | Size at LOD2 | Size at LOD4 | Protection |", "| ---: | ---: | ---: | ---: | ---: | --- |"])
            if report.get("partPixelLimit",0) > 0:
                lines.insert(len(lines)-2,f"Removal size limit in this run: {report['partPixelLimit']:.2f} px; area budget 2%; source triangle budget 20%. This threshold is experimental if it differs from the 2 px default.")
            for part in report["parts"]:
                lines.append(f"| {part['id']} | {part['triangles']} | {part['areaFraction']:.3%} | {part['pixelsLod2']:.2f} px | {part['pixelsLod4']:.2f} px | {part.get('protectedReason') or 'user-selectable'} |")
            for c in report["captures"]:
                if c['variant'].startswith('parts-') and c['view'] == 'front':
                    lines.extend(["", f"{c['variant']}: removed {c.get('removedParts',0)} parts / {c.get('removedPartTris',0)} source tris; removed area {c.get('removedPartAreaFraction',0):.3%}; estimated max size {c.get('removedPartMaxPixels',0):.2f} px. Sampled attribute errors refer to the retained surface; GPU differences compare the entire original mesh."])
        if notes:
            lines.extend(["", "Generation notes: " + " ".join(notes)])
        lines.extend(["", f"![{report['fixture']}]({report['fixture']}-oblique-review.png)", ""])
    lines.extend(["## Measurement limits", "", "Two orthographic views at 384 pixels on one GPU. No distance transitions, gameplay resolution, animated/skinned pose, "
                  "real scene lighting, original shader parity, or complete FBX assembly quality is established. "
                  "Screen-space field differences also include changed surface projection. Surface RGBA is sampled by the LOD0 correspondence validator. "
                  "Maximum differences and silhouette mismatch should be judged alongside the images. The optional selection score ranks candidates by the documented heuristic; it is not an absolute quality grade."])
    (directory / "report.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
    print(f"Saved real-model comparison sheets and report.md in {directory}")


if __name__ == "__main__":
    main()
