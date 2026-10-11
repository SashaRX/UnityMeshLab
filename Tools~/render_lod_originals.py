"""Compare captured authored FBX LODs, optionally with a generated far LOD."""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from render_lod_far_refinement import coverage, frame
from render_lod_screen_acceptance import readback


def display(raw):
    rgb = np.maximum(raw[:, :, :3], 0)
    rgb = np.where(rgb <= .0031308, 12.92*rgb, 1.055*rgb**(1/2.4)-.055)
    return Image.fromarray(np.uint8(np.clip(rgb, 0, 1)*255)).transpose(Image.Transpose.FLIP_TOP_BOTTOM)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--generated', type=Path)
    args = parser.parse_args()
    report = json.loads((args.directory/'fire-shield-original-lods.json').read_text())
    rows = [(args.directory, f'original-lod{i}', f'FBX LOD{i}: {row["triangles"]} tris')
            for i, row in enumerate(report['lods'])]
    if args.generated:
        generated = json.loads((args.generated/'fire-shield-shared-lightmap.json').read_text())
        rows.append((args.generated, 'uv2-min16', f'Generated LOD2: {generated["targetTriangles"]} tris'))
    width = 20+len(rows)*270
    detail = Image.new('RGB', (width, 730), '#20242a')
    small = Image.new('RGB', (width, 1150), '#20242a')
    d, s = ImageDraw.Draw(detail), ImageDraw.Draw(small)
    d.text((15, 12), 'Actual FireShield FBX LODs: gray diagnostic shading and wire; source-aligned camera.', fill='white')
    s.text((15, 12), 'FireShield: 64px maximum source extent, shown 3x. Same framing for every LOD.', fill='white')
    s.text((15, 32), 'Gray headlight / actual shared Bakery directional irradiance. Original PBR material is not evaluated.', fill='white')
    metrics = []
    for v, view in enumerate(('front', 'oblique')):
        source_mask = coverage(args.directory, 'fire-shield', 'original-lod0', view)
        source_small = frame(source_mask[:, :, None], source_mask)[:, :, 0]
        if args.generated:
            previous_source = coverage(args.generated, 'fire-shield', 'source', view)
            assert np.max(np.abs(previous_source-source_mask)) < .001, 'Generated comparison uses a different source framing'
        ys, xs = np.where(source_mask >= .5)
        extent = max(xs.max()-xs.min()+1, ys.max()-ys.min()+1)*1.15
        cx, cy = (xs.min()+xs.max()+1)/2, (ys.min()+ys.max()+1)/2
        crop = tuple(round(value) for value in (cx-extent/2,cy-extent/2,cx+extent/2,cy+extent/2))
        for col, (folder, variant, label) in enumerate(rows):
            x = 15+col*270
            mode = 'wire' if folder == args.directory else 'shaded'
            raw = readback(folder/f'fire-shield-{variant}-{view}-{mode}.rgba')
            d.text((x, 50+v*340), f'{label} | {view}', fill='white')
            # Crop all detail panels to the same source-aligned square.
            detail_pic = display(raw).transpose(Image.Transpose.FLIP_TOP_BOTTOM).crop(crop).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
            detail.paste(detail_pic.resize((250,250),Image.Resampling.LANCZOS), (x, 75+v*340))
            target_mask = coverage(folder, 'fire-shield', variant, view)
            target_small = frame(target_mask[:, :, None], source_mask)[:, :, 0]
            error = float(np.abs(target_small-source_small).sum()/np.maximum(target_small,source_small).sum())
            metrics.append({'variant':variant, 'view':view, 'coverageError64':error})
            for m, mode in enumerate(('shaded', 'baked-directional')):
                y = 65+v*535+m*265
                raw = readback(folder/f'fire-shield-{variant}-{view}-{mode}.rgba')
                pic = display(frame(raw,source_mask)).resize((216,216),Image.Resampling.NEAREST)
                s.text((x,y),f'{label} | {view}', fill='white')
                s.text((x,y+18), 'Headlight' if m == 0 else 'Shared Bakery irradiance', fill='white')
                small.paste(pic,(x,y+38))
    detail.save(args.directory/'original-lods-wire.png')
    small.save(args.directory/'original-lods-64px.png')
    (args.directory/'comparison-64px.json').write_text(json.dumps(metrics,indent=2))
    print(json.dumps(metrics,indent=2))


if __name__ == '__main__':
    main()
