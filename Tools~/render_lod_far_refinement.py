"""Audit and plot fresh 64px far-LOD refinement against the preceding far stage."""
import argparse
import json
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw
from render_lod_far_budget import audit, digest
from render_lod_screen_acceptance import readback

BACKGROUND = np.array([.012,.016,.024,0],dtype=np.float32)


def coverage(directory, fixture, variant, view):
    raw = readback(directory/f"{fixture}-{variant}-{view}-coverage.rgba")[:, :, 0]
    return np.clip((raw-BACKGROUND[0])/(1-BACKGROUND[0]),0,1)


def frame(raw, source_coverage):
    ys, xs = np.where(source_coverage >= .5)
    extent = max(xs.max()-xs.min()+1, ys.max()-ys.min()+1)
    scale = 64/extent
    cx, cy = (xs.min()+xs.max()+1)/2, (ys.min()+ys.max()+1)/2
    # A padded source frame retains added coverage outside its original outline.
    box = (round(cx-36/scale), round(cy-36/scale), round(cx+36/scale), round(cy+36/scale))
    return np.stack([np.asarray(Image.fromarray(raw[:, :, c].astype(np.float32)).crop(box).resize((72,72),Image.Resampling.BOX))
                     for c in range(raw.shape[2])],axis=2)


def compare_gpu(directory, previous, fixture):
    comparisons = []
    for view in ("front", "oblique"):
        source_mask = coverage(directory,fixture,"source",view)
        source_rgba = readback(directory/f"{fixture}-source-{view}-color.rgba")
        ref_mask = frame(source_mask[:, :, None],source_mask)[:, :, 0]
        ref = frame(source_rgba-BACKGROUND*(1-source_mask[:, :, None]),source_mask)/np.maximum(ref_mask[:, :, None],1e-20)
        source_lit = readback(directory/f"{fixture}-source-{view}-shaded.rgba")
        ref_lit = frame(source_lit-BACKGROUND*(1-source_mask[:, :, None]),source_mask)/np.maximum(ref_mask[:, :, None],1e-20)
        fields = {}
        for name, folder in (("before",previous),("after",directory)):
            mask = coverage(folder,fixture,"far-lod2",view)
            rgba = readback(folder/f"{fixture}-far-lod2-{view}-color.rgba")
            cov = frame(mask[:, :, None],source_mask)[:, :, 0]
            target = frame(rgba-BACKGROUND*(1-mask[:, :, None]),source_mask)/np.maximum(cov[:, :, None],1e-20)
            weight = np.minimum(cov,ref_mask)
            squared = np.sum((target-ref)**2*weight[:, :, None],axis=(0,1))/max(weight.sum(),1e-20)
            lit = readback(folder/f"{fixture}-far-lod2-{view}-shaded.rgba")
            target_lit = frame(lit-BACKGROUND*(1-mask[:, :, None]),source_mask)/np.maximum(cov[:, :, None],1e-20)
            shading = np.sum((target_lit[:, :, :3]-ref_lit[:, :, :3])**2*weight[:, :, None])/(3*max(weight.sum(),1e-20))
            fields[name] = {"coverageError": float(np.abs(cov-ref_mask).sum()/np.maximum(cov,ref_mask).sum()),
                            "rgbaRms": float(np.sqrt(squared.mean())), "rgbaChannelRms": squared**.5,"shadingRms": float(np.sqrt(shading))}
            fields[name]["rgbaChannelRms"] = fields[name]["rgbaChannelRms"].tolist()
        comparisons.append({"view": view, **fields})
    return comparisons


def graph(models, output):
    fig, axes = plt.subplots(1,4,figsize=(17,8))
    for i, policy in enumerate(("before", "after")):
        y = np.arange(len(models))+(i-.5)*.32
        rows = [[100*m[policy+"Triangles"]/m["sourceTriangles"] for m in models]]
        rows += [[m[policy+"64"][field]*(100 if field=="silhouetteMean" else 1)
                  if field!="rgbaRms" or m[policy+"64"]["rgbaEvaluated"] else np.nan for m in models]
                 for field in ("silhouetteMean", "normalRms", "rgbaRms")]
        for axis, values in zip(axes,rows):
            axis.barh(y,values,height=.3,label=policy)
            for row, value in enumerate(values):
                if np.isnan(value): axis.text(0,y[row],"n/a",va="center",fontsize=8)
    for col, axis in enumerate(axes):
        axis.set_yticks(np.arange(len(models)),[m["fixture"] for m in models] if col==0 else [])
        axis.set_ylim(len(models)-.5,-.5); axis.grid(axis="x",alpha=.2)
        axis.set_title(("Source triangles retained, %","Mean coverage error, %","Worst-view normal RMS, degrees","Worst-view linear RGBA RMS")[col],fontsize=11)
    axes[0].axvline(100/9,color="black",ls="--",lw=1); axes[0].legend()
    fig.suptitle("Far LOD2 refinement on the same eight project models\nPrevious far budget / refined far budget, six CPU views at 64px maximum object extent",fontsize=14)
    fig.tight_layout(rect=(0,0,1,.93)); fig.savefig(output,dpi=130); plt.close(fig)


def gallery(directory, previous, models, output, colors=False):
    rows = [m for m in models if m["after64"]["rgbaEvaluated"]] if colors else models
    columns = 3 if colors else 4
    canvas = Image.new("RGB",(columns*255+20,95+245*len(rows)),"#20242a"); draw = ImageDraw.Draw(canvas)
    draw.text((15,12),"Actual project vertex RGB: source / previous far LOD2 / refined far LOD2" if colors else
              "Actual project meshes: source / previous far LOD2 / refined far LOD2 / source coverage difference",fill="white")
    draw.text((15,32),"64px maximum source extent, shown 3x; aspect ratio and 4px padding retained.",fill="white")
    draw.text((15,52),"Fresh GPU diagnostic passes; original project materials/cameras are not evaluated.",fill="white")
    draw.text((15,72),"Linear RGB clamped only for display; metrics use Float32 RGBA." if colors else "Red: missing source coverage. Cyan: added coverage.",fill="white")
    for row, model in enumerate(rows):
        name = model["fixture"]
        source_mask = coverage(directory,name,"source","oblique")
        for col, (folder, variant, count) in enumerate(((directory,"source",model["sourceTriangles"]),
                    (previous,"far-lod2",model["beforeTriangles"]),(directory,"far-lod2",model["afterTriangles"]))):
            raw = readback(folder/f"{name}-{variant}-oblique-{'color' if colors else 'shaded'}.rgba")
            if colors:
                mask = coverage(folder,name,variant,"oblique")[:, :, None]
                raw = raw-BACKGROUND*(1-mask)+.13*(1-mask)
            tile = frame(raw,source_mask)[:, :, :3]
            pic = Image.fromarray(np.uint8(np.clip(tile,0,1)*255)).transpose(Image.Transpose.FLIP_TOP_BOTTOM).resize((216,216),Image.Resampling.NEAREST)
            x,y = 15+col*255,95+row*245
            draw.text((x,y),f"{name} | {count} tris",fill="white"); canvas.paste(pic,(x,y+22))
        if not colors:
            target = coverage(directory,name,"far-lod2","oblique")
            delta = frame((target-source_mask)[:, :, None],source_mask)[:, :, 0]
            difference = np.full((72,72,3),32,dtype=np.uint8)
            difference[delta < -.05] = (250,65,65); difference[delta > .05] = (50,210,240)
            pic = Image.fromarray(difference).transpose(Image.Transpose.FLIP_TOP_BOTTOM).resize((216,216),Image.Resampling.NEAREST)
            x,y = 15+3*255,95+row*245
            draw.text((x,y),"refined vs source coverage",fill="white"); canvas.paste(pic,(x,y+22))
    canvas.save(output)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory",type=Path); parser.add_argument("--previous",required=True,type=Path)
    parser.add_argument("--baseline-report",type=Path,default=Path("Documentation~/LOD_FAR_BUDGET_RESULTS.json"))
    parser.add_argument("--output",required=True,type=Path); args=parser.parse_args()
    result=audit(args.directory,args.previous)
    baseline={m["fixture"]:m for m in json.loads(args.baseline_report.read_text(encoding="utf-8"))["models"]}
    result["baselineCommit"]="df18859ceaef1045b0566cde8860015a72288fb1"
    for model in result["models"]:
        fixture=model["fixture"]
        previous=json.loads((args.previous/f"{fixture}-metrics.json").read_text(encoding="utf-8"))
        current=json.loads((args.directory/f"{fixture}-metrics.json").read_text(encoding="utf-8"))
        before=next(c for c in previous["captures"] if c["variant"]=="far-lod2" and c["view"]=="oblique")
        recorded=baseline[fixture]
        assert before["triangles"]==recorded["farTriangles"] and before["farComparison"]==recorded["far64"]
        assert recorded["sourceSha256"]==model["sourceSha256"]
        for view in ("front","oblique"):
            for kind in ("color","coverage","shaded"):
                suffix=f"{view}-{kind}.rgba"
                assert digest(args.previous/f"{fixture}-far-lod2-{suffix}")==recorded["farRawSha256"][suffix]
        after=next(c for c in current["captures"] if c["variant"]=="far-lod2" and c["view"]=="oblique")
        model.update(beforeTriangles=before["triangles"],afterTriangles=after["triangles"],before64=before["farComparison"],after64=after["farComparison"],
                     refinementNote=after["reductionNote"],attributeFit=after["attributeFit"],gpu64=compare_gpu(args.directory,args.previous,fixture))
        fit=after["attributeFit"]
        if fit and fit["screenColorVerification"] and fit["colorsAccepted"]:
            assert fit["visibleRgbaAfter"] <= fit["visibleRgbaBefore"]+1e-6
            for ch in "xyzw": assert fit["visibleChannelsAfter"][ch] <= fit["visibleChannelsBefore"][ch]+1e-6
        for view in ("front","oblique"):
            for kind in ("color","coverage","shaded"):
                path=f"{fixture}-far-lod1-{view}-{kind}.rgba"
                assert digest(args.directory/path)==digest(args.previous/path),"Near LOD changed: "+path
        if fixture in ("wrench","fire-shield"):
            assert model["after64"]["silhouetteMean"] < model["before64"]["silhouetteMean"]
            assert model["after64"]["silhouetteMax"] < model["before64"]["silhouetteMax"]
            assert "Far silhouette refit accepted" in model["refinementNote"]
        if fixture=="tent-colored": assert model["after64"]["rgbaRms"] < model["before64"]["rgbaRms"]
    args.output.with_suffix(".json").write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    graph(result["models"],args.output.with_name(args.output.name+"_ERRORS.png"))
    gallery(args.directory,args.previous,result["models"],args.output.with_name(args.output.name+"_MODELS.png"))
    gallery(args.directory,args.previous,result["models"],args.output.with_name(args.output.name+"_COLORS.png"),True)
    print(json.dumps({"tests":result["tests"],"counts":{m["fixture"]:m["afterTriangles"] for m in result["models"]}}))


if __name__=="__main__": main()
