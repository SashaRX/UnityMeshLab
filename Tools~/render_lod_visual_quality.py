"""Make review sheets from exact Unity GPU readbacks, without recreating geometry.

Run LodVisualQualityTests with MESHLAB_LOD_VISUAL_OUTPUT, then pass that directory.
PNG captures are display encoded; float .rgba captures are linear numeric fields.
"""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image


def raw(path):
    data = path.read_bytes()
    width, height = np.frombuffer(data, dtype="<i4", count=2)
    pixels = np.frombuffer(data, dtype="<f4", offset=8)
    if pixels.size != width * height * 4:
        raise ValueError(f"Invalid readback size: {path}")
    return pixels.reshape(height, width, 4)


def interior(mask):
    result = mask.copy()
    for y in range(-2, 3):
        for x in range(-2, 3):
            result &= np.roll(mask, (y, x), axis=(0, 1))
    result[:2] = result[-2:] = False
    result[:, :2] = result[:, -2:] = False
    return result


def difference(directory, fixture, variant, view):
    def path(name, mode):
        return directory / f"{fixture}-{name}-{view}-{mode}.rgba"
    source = raw(path("source", "color"))
    target = raw(path(variant, "color"))
    visible = interior((raw(path("source", "coverage"))[..., 0] > .5) &
                       (raw(path(variant, "coverage"))[..., 0] > .5))
    error = np.max(np.abs(source - target), axis=2).copy()
    error[~visible] = np.nan
    return np.flipud(error)


NAMES = {"source": "LOD0", "fast-lod1": "Fast · LOD1", "fast-lod2": "Fast · LOD2",
         "high-lod1": "High · LOD1", "high-lod2": "High · LOD2",
         "unchecked-triangles": "Triangles · без защиты цвета"}
TITLES = {"gradient": "Плавный цветовой градиент", "mask": "Узкая RGBA-маска",
          "curved": "Сильный изгиб · ограничения останавливают упрощение",
          "curved-soft": "Мягкий изгиб · фактическое упрощение"}


def sheet(directory, report, variants, view, suffix):
    fixture = report["fixture"]
    mode = "alpha" if fixture == "mask" else "shaded"
    row_names = ["Vertex color", "Сетка", "Альфа-маска" if fixture == "mask" else "Освещение", "Ошибка RGBA"]
    fig, axes = plt.subplots(4, len(variants), figsize=(4 * len(variants), 13), squeeze=False)
    fig.subplots_adjust(left=.06, right=.97, top=.9, bottom=.13, hspace=.04, wspace=.025)
    lookup = {(c["variant"], c["view"]): c for c in report["captures"]}
    cmap = matplotlib.colormaps["inferno"].copy()
    cmap.set_bad("#080c12")
    for col, variant in enumerate(variants):
        metrics = lookup[variant, view]
        for row, channel in enumerate(("color", "wire", mode)):
            pixels = Image.open(directory / f"{fixture}-{variant}-{view}-{channel}.png")
            axes[row, col].imshow(pixels)
        error_image = axes[3, col].imshow(difference(directory, fixture, variant, view), cmap=cmap, vmin=0, vmax=.05)
        for row in range(4):
            axes[row, col].set_xticks([])
            axes[row, col].set_yticks([])
            for spine in axes[row, col].spines.values():
                spine.set_visible(False)
        budget = f" · цель {metrics['targetTriangles']}" if variant != "source" else ""
        axes[0, col].set_title(f"{NAMES[variant]}\n{metrics['triangles']} треугольников{budget}", fontsize=11)
        axes[3, col].set_xlabel(f"max {metrics['maxPixelRgbaError']:.4f} · RMS {metrics['rmsPixelRgbaError']:.4f}", fontsize=10)
    for row, title in enumerate(row_names):
        axes[row, 0].set_ylabel(title, fontsize=12)
    bar = fig.colorbar(error_image, ax=list(axes[3]), orientation="horizontal", fraction=.055, pad=.1, shrink=.7)
    bar.set_label("Максимальное отклонение канала RGBA: 0 … 0.05 (значения выше 0.05 насыщены)", fontsize=10)
    fig.suptitle(f"{TITLES[fixture]} · {'спереди' if view == 'front' else 'под углом'}\n"
                 f"Реальные кадры Unity {report['unity']} · {report['graphicsApi']} · {report['resolution']}×{report['resolution']}",
                 fontsize=15, y=.97)
    fig.text(.5, .02, "Ошибка измерена в линейном RGBA внутри общей видимой поверхности; край 2 px исключён.\n"
             "Кадры показаны в sRGB. Сетка — отдельный диагностический overlay со смещением к камере 0.002 ед.",
             ha="center", fontsize=10)
    destination = directory / f"{fixture}-{suffix}.png"
    fig.savefig(destination, dpi=125)
    plt.close(fig)
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    directory = args.directory.resolve()
    reports = [json.loads((directory / f"{name}-metrics.json").read_text(encoding="utf-8"))
               for name in ("gradient", "mask", "curved", "curved-soft")]
    lines = ["# LOD visual GPU evaluation", "", "These are exact Unity render captures of synthetic fixtures, not generated illustrations.", "",
             "The field metric compares linear RGBA at matching screen pixels within common coverage, excluding a 2px border. "
             "On a curved mesh it includes projected geometry changes as well as attribute interpolation. "
             "Mesh-space source correspondence is measured separately by the LOD validator.", "",
             "The unchecked triangle mask result is a positive control with color costs and final validation disabled. "
             "It is not an accepted protected LOD.", "",
             "| Fixture | View | Variant | Tris / target | Selected | Max RGBA | RMS RGBA | Silhouette mismatch | RMS shading | Simplify ms |",
             "| --- | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |"]
    for report in reports:
        fixture = report["fixture"]
        all_variants = ["source", "fast-lod1", "high-lod1", "fast-lod2", "high-lod2"]
        if fixture == "mask":
            all_variants.append("unchecked-triangles")
        for view in ("front", "oblique"):
            sheet(directory, report, all_variants, view, f"{view}-full")
        compact = ["source", "high-lod2", "unchecked-triangles"] if fixture == "mask" else ["source", "fast-lod2", "high-lod2"]
        sheet(directory, report, compact, "oblique" if fixture.startswith("curved") else "front", "review")
        for c in report["captures"]:
            selected = f"{c['selectedCandidate']}/{c['candidateCount']}" if c["candidateCount"] else "-"
            lines.append(f"| {fixture} | {c['view']} | {c['variant']} | {c['triangles']} / {c['targetTriangles']} | {selected} | "
                         f"{c['maxPixelRgbaError']:.5f} | {c['rmsPixelRgbaError']:.5f} | {c['silhouetteMismatch']:.5f} | "
                         f"{c['rmsShadingError']:.5f} | {c['simplifyMs']:.1f} |")
    for report in reports:
        fixture = report["fixture"]
        lines.extend(["", f"![{fixture}]({fixture}-review.png)", ""])
    lines.extend(["## Limits", "", "This is a built-in-pipeline synthetic evaluation at one orthographic resolution and two views. "
                  "It does not establish production-asset quality, URP material parity, textured/normal-mapped appearance, "
                  "perspective distance transitions or categorical region-ID preservation. "
                  "Simplification timing is a single local run and is not a stable benchmark."])
    (directory / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Saved 12 comparison sheets and report.md in {directory}")


if __name__ == "__main__":
    main()
