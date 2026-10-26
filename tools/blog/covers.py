"""Draws each post's cover (docs/img/blog/<slug>.webp, 1200x800, and <slug>-600.webp) from covers.json.

  python tools/blog/covers.py        (needs Chrome and puppeteer-core; set NM to a node_modules holding it)

Picture covers are a crop of a real app picture from docs/img (the showcase copy, never real data),
sat on the site's dark ground. Tile covers are drawn like the app's own tiles, so a number always
comes with what it is.
"""
import html, json, os, re, subprocess, tempfile
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
DOCS = HERE.parents[1] / "docs"
OUT = DOCS / "img" / "blog"
COL = {"cpu": "#5b8def", "gpu": "#22d3ee", "hot": "#a78bfa"}
FONT = (DOCS / "fonts" / "geist-latin.woff2").as_uri()
MONO = (DOCS / "fonts" / "geist-mono-latin.woff2").as_uri()

BASE = f"""<style>
@font-face {{ font-family: G; src: url("{FONT}"); font-weight: 300 700; }}
@font-face {{ font-family: M; src: url("{MONO}"); font-weight: 400 500; }}
* {{ box-sizing: border-box; margin: 0; }}
body {{ width: 1200px; height: 800px; background: #050607; font-family: G, "Segoe UI", sans-serif; color: #f3f4f6; overflow: hidden;
  display: grid; place-items: center; position: relative; }}
body::before {{ content: ""; position: absolute; inset: 0; background: radial-gradient(900px 500px at 50% 0%, var(--glow), transparent 70%); }}
.pic {{ position: relative; max-width: 1080px; max-height: 680px; border-radius: 20px; border: 1px solid rgba(255,255,255,.1);
  box-shadow: 0 40px 80px rgba(0,0,0,.6); display: block; }}
.tiles {{ position: relative; display: flex; gap: 28px; padding: 0 56px; width: 100%; justify-content: center; }}
.tile {{ flex: 1 1 0; max-width: 960px; background: #111317; border: 1px solid rgba(255,255,255,.1); border-radius: 26px; padding: 44px 46px 46px; }}
.k {{ font: 500 32px/1.25 M, monospace; letter-spacing: .05em; text-transform: uppercase; color: #9aa1ab; }}
.v {{ font: 600 var(--vs, 150px)/1 G, sans-serif; letter-spacing: -0.05em; color: var(--c); margin: 26px 0 22px; white-space: nowrap; }}
.s {{ font: 400 38px/1.3 G, sans-serif; color: #c5cad2; }}
.chips {{ position: relative; padding: 0 64px; width: 100%; }}
.chip {{ display: flex; align-items: center; gap: 26px; background: #111317; border: 1px solid rgba(255,255,255,.1); border-radius: 22px;
  padding: 34px 40px; margin-top: 20px; font: 600 54px/1.15 G, sans-serif; letter-spacing: -0.02em; }}
.chip b {{ width: 22px; height: 22px; border-radius: 50%; background: var(--c); flex: none; }}
.letters {{ display: flex; gap: 20px; margin: 30px 0 34px; }}
.l {{ flex: 1; height: 380px; background: #111317; border: 1px solid rgba(255,255,255,.1); border-radius: 22px; display: flex; flex-direction: column;
  justify-content: flex-end; align-items: center; padding-bottom: 22px; position: relative; overflow: hidden; font: 500 46px/1 M, monospace; color: #9aa1ab; }}
.l i {{ position: absolute; left: 0; right: 0; bottom: 0; background: var(--c); opacity: .28; }}
.l.win {{ color: #fff; border-color: var(--c); }}
.l.pick {{ color: #fff; border: 2px solid #34d399; }}
.l span {{ position: relative; }}
.l em {{ position: relative; font-style: normal; font: 600 40px/1 G; color: #fff; margin-bottom: 14px; }}
</style>"""


def page(slug, c):
    colour = COL.get(c.get("colour", "gpu"))
    glow = {"#5b8def": "rgba(91,141,239,.16)", "#22d3ee": "rgba(34,211,238,.14)", "#a78bfa": "rgba(167,139,250,.17)"}[colour]
    head = f'<!doctype html><meta charset="utf-8">{BASE}<body style="--c:{colour};--glow:{glow}">'
    if "img" in c:
        src = DOCS / "img" / f"{c['img']}.webp"
        x, y, w, h = c["crop"]
        im = Image.open(src).convert("RGB").crop((x, y, x + w, y + h))
        tmp = Path(tempfile.gettempdir()) / f"cover-{slug}.png"
        im.save(tmp)
        scale = min(1080 / w, 680 / h, 2.0)
        return head + \
            f'<img class="pic" src="{tmp.as_uri()}" style="width:{round(w * scale)}px;height:{round(h * scale)}px">'
    if "tiles" in c:
        n = len(c["tiles"])
        vs = {1: 250, 2: 128, 3: 60}[n]
        body = "".join(f'<div class="tile"><p class="k">{html.escape(k)}</p><p class="v" style="--vs:{vs}px">{html.escape(v)}</p><p class="s">{html.escape(s)}</p></div>'
                       for k, v, s in c["tiles"])
        return head + f'<div class="tiles">{body}</div>'
    if "chips" in c:
        chips = "".join(f'<div class="chip"><b></b>{html.escape(t)}</div>' for t in c["chips"])
        return head + f'<div class="chips"><p class="k">{html.escape(c["chips_label"])}</p>{chips}</div>'
    if "letters" in c:
        top = max(c["votes"].values())
        ls = ""
        for L in c["letters"]:
            v = c["votes"].get(L, 0)
            cls = "win" if v == top else ("pick" if L == "E" else "")
            ls += f'<div class="l {cls}"><i style="height:{round(100 * v / top)}%"></i>{"<em>" + str(v) + "</em>" if v else ""}<span>{L}</span></div>'
        return head + f'<div class="chips"><p class="k">{html.escape(c["label"])}</p><div class="letters">{ls}</div><p class="s">{html.escape(c["sub"])}</p></div>'
    raise SystemExit(f"{slug}: no cover kind")


SHOOT = r"""
const pc = require(process.env.NM + '/puppeteer-core');
const jobs = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));
(async () => {
  const b = await pc.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: 'new', args: ['--allow-file-access-from-files'] });
  const p = await b.newPage();
  await p.setViewport({ width: 1200, height: 800, deviceScaleFactor: 1 });
  for (const [htmlFile, png] of jobs) {
    await p.goto('file:///' + htmlFile.replace(/\\/g, '/'), { waitUntil: 'load' });
    await p.evaluate(() => document.fonts.ready);
    await p.screenshot({ path: png });
  }
  await b.close();
})();
"""


def main():
    spec = json.loads((HERE / "covers.json").read_text(encoding="utf-8"))
    for f in sorted((HERE / "guides").glob("*.html")):
        m = re.match(r"\s*<!--meta\s*(\{.*?\})\s*-->", f.read_text(encoding="utf-8"), re.S)
        g = json.loads(m.group(1))
        if g.get("cover_img"):
            spec[g["slug"]] = {"img": g["cover_img"], "crop": g["cover_crop"]}
        elif g.get("cover_tiles"):
            spec[g["slug"]] = {"colour": "gpu", "tiles": g["cover_tiles"]}
    only = set(os.environ.get("ONLY", "").split(",")) - {""}
    if only:
        spec = {k: v for k, v in spec.items() if k in only}
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = Path(tempfile.gettempdir()) / "rigsight-covers"
    tmp.mkdir(exist_ok=True)
    jobs = []
    for slug, c in spec.items():
        if slug == "_":
            continue
        f = tmp / f"{slug}.html"
        f.write_text(page(slug, c), encoding="utf-8")
        jobs.append([str(f), str(tmp / f"{slug}.png")])
    (tmp / "jobs.json").write_text(json.dumps(jobs), encoding="utf-8")
    (tmp / "shoot.cjs").write_text(SHOOT, encoding="utf-8")
    subprocess.run(["node", str(tmp / "shoot.cjs"), str(tmp / "jobs.json")], check=True, env=os.environ)
    for f, png in jobs:
        slug = Path(f).stem
        im = Image.open(png).convert("RGB")
        im.save(OUT / f"{slug}.webp", "WEBP", quality=86, method=6)
        im.resize((600, 400), Image.LANCZOS).save(OUT / f"{slug}-600.webp", "WEBP", quality=84, method=6)
    print(f"{len(jobs)} covers -> {OUT}")


if __name__ == "__main__":
    main()
