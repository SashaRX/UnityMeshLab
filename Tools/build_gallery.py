#!/usr/bin/env python3
"""
build_gallery.py — render an HTML voting gallery for BenchmarkReports.

Usage:
    python build_gallery.py <BenchmarkReports-dir> [--out <output-dir>]

Produces:
    <out>/_gallery_index.html
    <out>/_gallery_<model>.html  (one per lodGroup)

Each cell = (model, renderer, lodIndex, atlasRes, shellPad). The HTML page
embeds metrics + a thumbnail of the result UV2 PNG. Use the keyboard or the
mouse to score cells:

    1..5     rate current cell (1=ugly, 3=neutral, 5=good); 0=clear
    g/b/u/n  shortcuts for good/bad/ugly/neutral
    Space    next unrated cell
    Tab/→    next cell, Shift+Tab/← previous
    t        toggle tag panel for current cell
    e        export votes JSON
    i        import votes JSON

Votes live in localStorage so they survive reloads. Hit "Export votes" at the
bottom to download a .json blob you can commit alongside the data.

The script discovers the BenchmarkReports layout by looking for *.csv files
matching *_sweep_res*_pad*_bdr*_*.csv next to *_png/ folders.
"""

import argparse, csv, glob, html, json, os, re, sys
from collections import defaultdict


CELL_PATTERN = re.compile(
    r"^(?P<ts>\d{8}_\d{6})_(?P<lodGroup>.+?)_sweep_res(?P<res>\d+)_pad(?P<pad>\d+)_bdr(?P<bdr>\d+)_.*?\.csv$"
)


def discover_cells(root):
    """Return list of dicts describing every sweep cell with its CSV rows + PNG dir."""
    cells = []
    for csv_path in sorted(glob.glob(os.path.join(root, "*_sweep_*.csv"))):
        m = CELL_PATTERN.match(os.path.basename(csv_path))
        if not m:
            continue
        png_dir = csv_path.replace(".csv", "_png")
        rows = []
        with open(csv_path, newline="", encoding="utf-8-sig") as f:
            for row in csv.DictReader(f):
                rows.append(row)
        cells.append(dict(
            csv=csv_path,
            png_dir=os.path.basename(png_dir),
            png_dir_exists=os.path.isdir(png_dir),
            ts=m.group("ts"),
            lodGroup=m.group("lodGroup"),
            res=int(m.group("res")),
            pad=int(m.group("pad")),
            bdr=int(m.group("bdr")),
            rows=rows,
            modeTag=os.path.basename(csv_path).split(f"bdr{m.group('bdr')}_", 1)[-1].rsplit(".", 1)[0],
        ))
    return cells


CSS = r"""
body{background:#181820;color:#ddd;font-family:system-ui,sans-serif;margin:18px;}
a{color:#9cf;}a:visited{color:#c9c9ff;}
h1,h2{border-bottom:1px solid #333;padding-bottom:4px;}
h2{margin-top:32px;}
table{border-collapse:collapse;margin:8px 0 24px;}
th,td{border:1px solid #333;padding:4px;text-align:center;font-size:11px;vertical-align:top;background:#222;}
th{background:#222;}
td.cell{position:relative;width:200px;}
td.capHit{outline:2px solid #f80;}
td.bad{background:#3a1818;}
td.lowUtil{box-shadow:inset 0 0 0 2px #f33;}
td.empty{color:#555;}
img{display:block;width:180px;height:180px;object-fit:contain;background:#0c0c10;cursor:pointer;}
.meta{font-size:10px;color:#aaa;line-height:1.3;margin-top:2px;text-align:left;}
.legend{font-size:12px;color:#999;margin:6px 0 16px;}
.nav a{margin-right:10px;}
/* Voting controls */
.vote-row{display:flex;justify-content:space-between;align-items:center;margin-top:3px;}
.vote-btns{display:flex;gap:2px;}
.vote-btn{cursor:pointer;width:18px;height:18px;font-size:11px;line-height:18px;border:1px solid #444;background:#1a1a1f;color:#888;text-align:center;border-radius:3px;}
.vote-btn:hover{background:#2a2a30;color:#ddd;}
.vote-btn.active{background:#3b6;color:#000;font-weight:bold;}
.vote-btn[data-rating="1"].active{background:#c33;color:#fff;}
.vote-btn[data-rating="2"].active{background:#e74;color:#fff;}
.vote-btn[data-rating="3"].active{background:#dd4;color:#000;}
.vote-btn[data-rating="4"].active{background:#9c4;color:#000;}
.vote-btn[data-rating="5"].active{background:#3b6;color:#000;}
.tag-toggle{cursor:pointer;font-size:11px;color:#888;border:1px solid #444;padding:1px 6px;border-radius:3px;background:#1a1a1f;}
.tag-toggle:hover{color:#ddd;}
.tag-panel{display:none;position:absolute;left:0;right:0;top:100%;z-index:5;background:#1a1a22;border:1px solid #555;padding:6px;margin-top:2px;text-align:left;}
.tag-panel.open{display:block;}
.tag-panel label{display:block;font-size:10px;color:#bbb;margin:1px 0;cursor:pointer;}
.tag-panel textarea{width:100%;background:#0c0c10;color:#ddd;border:1px solid #333;font-size:10px;margin-top:4px;height:32px;}
td.cell.rate-1{box-shadow:inset 0 0 0 3px #c33;}
td.cell.rate-2{box-shadow:inset 0 0 0 3px #e74;}
td.cell.rate-3{box-shadow:inset 0 0 0 3px #dd4;}
td.cell.rate-4{box-shadow:inset 0 0 0 3px #9c4;}
td.cell.rate-5{box-shadow:inset 0 0 0 3px #3b6;}
td.cell.current{outline:3px solid #59f;outline-offset:-3px;}
/* Bottom bar */
#bar{position:sticky;bottom:0;background:#222;border-top:1px solid #444;padding:8px 12px;margin:24px -18px -18px;display:flex;gap:14px;align-items:center;flex-wrap:wrap;}
#bar button{background:#2a2a30;color:#ddd;border:1px solid #555;padding:5px 10px;cursor:pointer;border-radius:3px;font-size:12px;}
#bar button:hover{background:#3a3a44;}
#bar .stat{font-size:12px;color:#9cf;}
#help{font-size:11px;color:#888;}
#help kbd{background:#222;border:1px solid #555;padding:0 4px;border-radius:2px;color:#ddd;font-family:monospace;}
"""

VOTE_TAGS = [
    "narrow_strips",
    "empty_atlas",
    "rotation_wrong",
    "stretched",
    "good_pack",
    "broken_shells",
    "small_shells",
    "overlap_visible",
]


JS = r"""
const VOTE_KEY = 'uvSweepVotes/' + (window.GALLERY_ID || 'default');
const TAGS = %TAGS_JSON%;

let votes = {};
try { votes = JSON.parse(localStorage.getItem(VOTE_KEY) || '{}'); } catch (e) { votes = {}; }
let cells = [];
let currentIdx = -1;

function saveVotes() {
    localStorage.setItem(VOTE_KEY, JSON.stringify(votes));
    refreshStats();
}

function applyVoteUI(td) {
    const id = td.dataset.cellId;
    const v = votes[id] || {};
    td.classList.remove('rate-1','rate-2','rate-3','rate-4','rate-5');
    if (v.rating) td.classList.add('rate-' + v.rating);
    td.querySelectorAll('.vote-btn').forEach(b => {
        b.classList.toggle('active', String(v.rating || 0) === b.dataset.rating);
    });
    const panel = td.querySelector('.tag-panel');
    if (panel) {
        panel.querySelectorAll('input[type=checkbox]').forEach(cb => {
            cb.checked = (v.tags || []).includes(cb.value);
        });
        const note = panel.querySelector('textarea');
        if (note) note.value = v.note || '';
    }
}

function setRating(id, r) {
    if (!votes[id]) votes[id] = {};
    if (r === 0) {
        delete votes[id].rating;
        if (!votes[id].rating && !(votes[id].tags||[]).length && !votes[id].note) delete votes[id];
    } else {
        votes[id].rating = r;
    }
    saveVotes();
    document.querySelectorAll('td.cell').forEach(applyVoteUI);
}

function toggleTag(id, tag) {
    if (!votes[id]) votes[id] = {};
    const t = votes[id].tags || [];
    const i = t.indexOf(tag);
    if (i >= 0) t.splice(i, 1); else t.push(tag);
    votes[id].tags = t;
    if (!t.length) delete votes[id].tags;
    if (!votes[id].rating && !(votes[id].tags||[]).length && !votes[id].note) delete votes[id];
    saveVotes();
}

function setNote(id, note) {
    if (!votes[id]) votes[id] = {};
    if (note.trim()) votes[id].note = note;
    else { delete votes[id].note;
           if (!votes[id].rating && !(votes[id].tags||[]).length) delete votes[id]; }
    saveVotes();
}

function setCurrent(idx) {
    if (currentIdx >= 0 && cells[currentIdx]) cells[currentIdx].classList.remove('current');
    currentIdx = (idx + cells.length) % cells.length;
    cells[currentIdx].classList.add('current');
    cells[currentIdx].scrollIntoView({block:'center', behavior:'smooth'});
}

function nextUnrated() {
    for (let i = 1; i <= cells.length; i++) {
        const j = (currentIdx + i) % cells.length;
        const id = cells[j].dataset.cellId;
        if (!votes[id] || !votes[id].rating) { setCurrent(j); return; }
    }
    alert('All cells rated.');
}

function refreshStats() {
    const total = cells.length;
    const rated = cells.filter(td => votes[td.dataset.cellId] && votes[td.dataset.cellId].rating).length;
    const stat = document.getElementById('stat');
    if (stat) stat.textContent = `${rated} / ${total} rated`;
}

function exportVotes() {
    const blob = new Blob([JSON.stringify({galleryId: window.GALLERY_ID, votes}, null, 2)],
                         {type:'application/json'});
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'votes_' + (window.GALLERY_ID || 'gallery') + '.json';
    a.click();
}

function importVotes(file) {
    const reader = new FileReader();
    reader.onload = e => {
        try {
            const data = JSON.parse(e.target.result);
            const incoming = data.votes || data;
            Object.assign(votes, incoming);
            saveVotes();
            document.querySelectorAll('td.cell').forEach(applyVoteUI);
        } catch (err) { alert('Bad JSON: ' + err.message); }
    };
    reader.readAsText(file);
}

function clearAll() {
    if (!confirm('Clear all votes for this gallery?')) return;
    votes = {};
    saveVotes();
    document.querySelectorAll('td.cell').forEach(applyVoteUI);
}

document.addEventListener('DOMContentLoaded', () => {
    cells = Array.from(document.querySelectorAll('td.cell'));
    cells.forEach((td, i) => {
        applyVoteUI(td);
        td.addEventListener('click', e => {
            if (e.target.tagName === 'IMG' || e.target.tagName === 'A') return;
            setCurrent(i);
        });
        td.querySelectorAll('.vote-btn').forEach(btn => {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                setCurrent(i);
                setRating(td.dataset.cellId, parseInt(btn.dataset.rating, 10));
            });
        });
        const tagToggle = td.querySelector('.tag-toggle');
        if (tagToggle) tagToggle.addEventListener('click', e => {
            e.stopPropagation();
            const panel = td.querySelector('.tag-panel');
            if (panel) panel.classList.toggle('open');
        });
        td.querySelectorAll('.tag-panel input[type=checkbox]').forEach(cb => {
            cb.addEventListener('change', e => {
                e.stopPropagation();
                toggleTag(td.dataset.cellId, cb.value);
            });
        });
        const note = td.querySelector('.tag-panel textarea');
        if (note) note.addEventListener('input', e => setNote(td.dataset.cellId, e.target.value));
    });
    if (cells.length) setCurrent(0);
    refreshStats();
    document.addEventListener('keydown', e => {
        if (e.target.tagName === 'TEXTAREA' || e.target.tagName === 'INPUT') return;
        if (currentIdx < 0) return;
        const id = cells[currentIdx].dataset.cellId;
        const k = e.key.toLowerCase();
        if (/^[0-5]$/.test(k))            { setRating(id, parseInt(k, 10)); }
        else if (k === 'g')               { setRating(id, 4); }
        else if (k === 'b')               { setRating(id, 2); }
        else if (k === 'u')               { setRating(id, 1); }
        else if (k === 'n')               { setRating(id, 3); }
        else if (k === ' ' || k === 'spacebar') { e.preventDefault(); nextUnrated(); }
        else if (k === 'tab')             { e.preventDefault(); setCurrent(e.shiftKey ? currentIdx-1 : currentIdx+1); }
        else if (k === 'arrowright')      { e.preventDefault(); setCurrent(currentIdx+1); }
        else if (k === 'arrowleft')       { e.preventDefault(); setCurrent(currentIdx-1); }
        else if (k === 't') {
            const panel = cells[currentIdx].querySelector('.tag-panel');
            if (panel) panel.classList.toggle('open');
        }
        else if (k === 'e') exportVotes();
    });
    const fileIn = document.getElementById('file-import');
    if (fileIn) fileIn.addEventListener('change', e => { if (e.target.files[0]) importVotes(e.target.files[0]); });
});
"""


def cell_html(cell, model, renderer, lod, res_axis, pad_axis, root_dir):
    """Return HTML rows for one (renderer, lod) — one row per atlasRes."""
    lookup = {}
    for c in cell.values():
        for r in c["rows"]:
            if r.get("isSourceLod") == "1":
                continue
            if r.get("rendererName") != renderer or int(r.get("lodIndex", -1)) != lod:
                continue
            key = (int(c["res"]), int(c["pad"]))
            lookup[key] = (c, r)

    out = []
    for res in res_axis:
        out.append(f'<tr><th>res={res}</th>')
        for pad in pad_axis:
            entry = lookup.get((res, pad))
            if not entry:
                out.append('<td class="empty cell" data-cell-id="">—</td>')
                continue
            c, r = entry
            cell_id = f"{model}|{renderer}|lod{lod}|res{res}|pad{pad}"
            inv = int(float(r.get("invertedCount", 0)))
            stt = int(float(r.get("stretchedCount", 0)))
            za  = int(float(r.get("zeroAreaCount", 0)))
            tex = float(r.get("texelDensityMedian", 0))
            topfx = int(float(r.get("topologyFixed", 0)))
            cap = r.get("topologyCapHit", "False").lower() in ("1","true")
            try: util = float(r.get("atlasUtilization", 0) or 0)
            except: util = 0.0
            png_name = f"{r.get('rendererName')}_LOD{lod}_uv2.png"
            png_path = os.path.join(c["png_dir"], png_name)
            full_png = os.path.join(root_dir, png_path)
            img_html = (f'<a href="{html.escape(png_path)}" target="_blank">'
                        f'<img src="{html.escape(png_path)}" loading="lazy"/></a>') \
                       if os.path.exists(full_png) else '<span style="color:#555">no png</span>'
            cls = ["cell"]
            if cap: cls.append("capHit")
            if za > 100: cls.append("bad")
            tag_panel = '<div class="tag-panel">'
            for t in VOTE_TAGS:
                tag_panel += f'<label><input type="checkbox" value="{t}"> {t}</label>'
            tag_panel += '<textarea placeholder="note (optional)"></textarea></div>'
            vote_buttons = ''.join(
                f'<span class="vote-btn" data-rating="{r}">{r}</span>'
                for r in (1,2,3,4,5)
            )
            util_label = (f'util={util*100:.0f}%' if util > 0 else 'util=?')
            if util > 0 and util < 0.5: cls.append('lowUtil')
            out.append(
                f'<td class="{ " ".join(cls)}" data-cell-id="{html.escape(cell_id)}">'
                f'{img_html}'
                f'<div class="meta">inv={inv} str={stt} 0A={za} {util_label}<br/>tex={tex:.0f} topFx={topfx}{"⛔" if cap else ""}</div>'
                f'<div class="vote-row">'
                f'  <div class="vote-btns">{vote_buttons}</div>'
                f'  <span class="tag-toggle" title="toggle tags">tags</span>'
                f'</div>'
                f'{tag_panel}'
                f'</td>'
            )
        out.append('</tr>')
    return "".join(out)


def render_index(out_dir, models, gallery_id):
    parts = [f'<!doctype html><html><head><meta charset="utf-8"><title>UV2 sweep gallery</title>',
             f'<style>{CSS}</style></head><body>',
             f'<h1>UV2 sweep gallery — {html.escape(gallery_id)}</h1>',
             '<p>Per-model galleries with voting (rating 1-5 + tags). Votes live in your browser; export at the bottom of each page.</p>',
             '<ul>']
    for m in models:
        parts.append(f'<li><a href="_gallery_{html.escape(m)}.html">{html.escape(m)}</a></li>')
    parts.append('</ul></body></html>')
    with open(os.path.join(out_dir, "_gallery_index.html"), "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


def render_model(out_dir, gallery_id, model, all_cells, res_axis, pad_axis, root_dir):
    cells_for_model = [c for c in all_cells if c["lodGroup"] == model]
    cells_by_res_pad = {(c["res"], c["pad"]): c for c in cells_for_model}

    # collect (renderer, lod) pairs
    pairs = set()
    for c in cells_for_model:
        for r in c["rows"]:
            if r.get("isSourceLod") == "1":
                continue
            pairs.add((r.get("rendererName"), int(r.get("lodIndex", -1))))
    pairs = sorted(pairs)

    nav = " | ".join(f'<a href="_gallery_{m}.html">{html.escape(m)}</a>' for m in MODELS_ORDER)
    nav += f' | <a href="_gallery_index.html">index</a>'

    parts = [f'<!doctype html><html><head><meta charset="utf-8"><title>UV2 — {html.escape(model)}</title>',
             f'<style>{CSS}</style></head><body>',
             f'<script>window.GALLERY_ID = {json.dumps(f"{gallery_id}/{model}")};</script>',
             f'<h1>UV2 sweep gallery — {html.escape(model)} <small>({html.escape(gallery_id)})</small></h1>',
             f'<div class="nav">{nav}</div>',
             '<div class="legend">Columns = <b>shellPad</b>, rows = <b>atlasRes</b>. '
             'Click a thumbnail for full-size. Use the 1-5 buttons or hotkeys. '
             '<span id="help"><kbd>1</kbd>-<kbd>5</kbd> rate · <kbd>g</kbd>/<kbd>b</kbd>/<kbd>u</kbd>/<kbd>n</kbd> shortcuts · '
             '<kbd>Space</kbd> next unrated · <kbd>Tab</kbd>/<kbd>→</kbd> next · <kbd>t</kbd> tags · <kbd>e</kbd> export</span>'
             '</div>']

    for renderer, lod in pairs:
        parts.append(f'<h2>{html.escape(renderer)} / LOD{lod}</h2>')
        parts.append('<table><thead><tr><th>res \\ pad</th>')
        for p in pad_axis: parts.append(f'<th>pad={p}</th>')
        parts.append('</tr></thead><tbody>')
        parts.append(cell_html(cells_by_res_pad, model, renderer, lod, res_axis, pad_axis, root_dir))
        parts.append('</tbody></table>')

    parts.append('<div id="bar">'
                 '<span class="stat" id="stat">…</span>'
                 '<button onclick="exportVotes()">Export votes</button>'
                 '<label><button onclick="document.getElementById(\'file-import\').click()">Import votes…</button>'
                 '<input id="file-import" type="file" accept=".json" hidden></label>'
                 '<button onclick="clearAll()">Clear all</button>'
                 '<span id="help">Hotkeys: 1-5/g/b/u/n rate · Space=next unrated · Tab next · t=tags · e=export</span>'
                 '</div>')

    js_inline = JS.replace("%TAGS_JSON%", json.dumps(VOTE_TAGS))
    parts.append(f'<script>{js_inline}</script>')
    parts.append('</body></html>')

    with open(os.path.join(out_dir, f"_gallery_{model}.html"), "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


MODELS_ORDER = []  # filled per call


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data_dir", help="BenchmarkReports folder containing *_sweep_*.csv")
    ap.add_argument("--out", default=None, help="output directory (default = data_dir)")
    ap.add_argument("--gallery-id", default=None, help="identifier (also localStorage key)")
    args = ap.parse_args()

    if not os.path.isdir(args.data_dir):
        print(f"Not a directory: {args.data_dir}", file=sys.stderr)
        sys.exit(1)
    out_dir = args.out or args.data_dir
    os.makedirs(out_dir, exist_ok=True)
    gallery_id = args.gallery_id or os.path.basename(os.path.normpath(args.data_dir))

    cells = discover_cells(args.data_dir)
    if not cells:
        print("No *_sweep_*.csv found.", file=sys.stderr)
        sys.exit(1)

    res_axis = sorted({c["res"] for c in cells})
    pad_axis = sorted({c["pad"] for c in cells})
    models = sorted({c["lodGroup"] for c in cells})

    global MODELS_ORDER
    MODELS_ORDER = models

    for m in models:
        render_model(out_dir, gallery_id, m, cells, res_axis, pad_axis, args.data_dir)
    render_index(out_dir, models, gallery_id)

    print(f"Wrote galleries → {out_dir}")
    print(f"Models: {models}")
    print(f"Cells: {len(cells)}")
    print(f"Open: {os.path.join(out_dir, '_gallery_index.html')}")


if __name__ == "__main__":
    main()
