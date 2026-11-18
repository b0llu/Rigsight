"""Draws each post's cover from covers2.json (since 2026-11-18): <slug>.webp 1200x675 (16:9, the page and Google),
<slug>-600.webp 600x338 (the blog list) and <slug>-og.jpg 1200x630 (link previews on Reddit, X and Discord).

  NM=<node_modules with puppeteer-core> python tools/blog/covers2.py [OUT=<folder>] [ONLY=slug,slug]

A: a subject photo from photos/ (openly licensed, credit in photos.json) under a dark fade, with a small category
   label and a short line. B: RigSight's own picture from docs/img (the showcase copy, never real data) on the dark
   ground, with the label, a number when the number is the hook, and a short line.
"""
import html, json, os, subprocess, tempfile
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
DOCS = HERE.parents[1] / "docs"
PHOTOS = HERE / "photos"
FONT = (DOCS / "fonts" / "geist-latin.woff2").as_uri()
MONO = (DOCS / "fonts" / "geist-mono-latin.woff2").as_uri()
LOGO = (DOCS / "img" / "logo-144.webp").as_uri()
COL = {"gpu": ("#22d3ee", "rgba(34,211,238,.18)"), "hot": ("#a78bfa", "rgba(167,139,250,.2)")}

CSS = f"""<style>
@font-face {{ font-family: G; src: url("{FONT}"); font-weight: 300 800; }}
@font-face {{ font-family: M; src: url("{MONO}"); font-weight: 400 600; }}
* {{ box-sizing: border-box; margin: 0; }}
html, body {{ width: 100vw; height: 100vh; overflow: hidden; }}
body {{ background: #050607; color: #f3f4f6; font-family: G, "Segoe UI", sans-serif; position: relative; }}
.brand {{ position: absolute; left: 5.2vw; top: 6.2vh; display: flex; align-items: center; gap: 1vw; font: 700 2.3vw/1 G; letter-spacing: -.02em; z-index: 3; }}
.brand img {{ width: 3vw; height: 3vw; border-radius: .7vw; }}
.label {{ font: 600 1.75vw/1 M, monospace; letter-spacing: .09em; text-transform: uppercase; color: var(--c); }}
.hook {{ font: 700 5.4vw/1.06 G; letter-spacing: -.035em; text-wrap: balance; }}
/* A: photo */
.photo {{ position: absolute; inset: 0; background: center / cover no-repeat var(--img); }}
.photo.contain {{ background: radial-gradient(70vw 60vh at 68% 50%, #1b2027, #050607 75%); }}
.photo.contain::after {{ content: ""; position: absolute; inset: 9vh 3vw 9vh 32vw; background: center / contain no-repeat var(--img);
  filter: drop-shadow(0 3vh 4vh rgba(0,0,0,.7)); }}
.fade {{ position: absolute; inset: 0; background: linear-gradient(90deg, rgba(4,5,6,.94) 0%, rgba(4,5,6,.82) 30%, rgba(4,5,6,.25) 62%, rgba(4,5,6,0) 80%),
  linear-gradient(0deg, rgba(4,5,6,.7) 0%, rgba(4,5,6,0) 38%); }}
.a-text {{ position: absolute; left: 5.2vw; bottom: 10vh; width: 52vw; z-index: 3; display: grid; gap: 2.6vh; }}
.a-text .bar {{ width: 5vw; height: .5vh; background: var(--c); border-radius: 1vh; }}
/* B: evidence */
.glow {{ position: absolute; inset: 0; background: radial-gradient(70vw 70vh at 75% 30%, var(--glow), transparent 70%); }}
.shot {{ position: absolute; right: -4vw; top: 12vh; width: 60vw; height: 84vh; border-radius: 1.6vw; border: 1px solid rgba(255,255,255,.14);
  background: no-repeat var(--img); background-size: var(--zoom, 175%); background-position: var(--pos, 22% 10%); box-shadow: 0 4vh 8vh rgba(0,0,0,.6); }}
.shot.card {{ background-size: var(--zoom, 120%); background-position: var(--pos, center); background-color: #0b0d10; }}
.b-text {{ position: absolute; left: 5.2vw; top: 22vh; width: 38vw; z-index: 3; display: grid; gap: 2.4vh; }}
.num {{ font: 700 8.6vw/1 G; letter-spacing: -.05em; color: var(--c); white-space: nowrap; }}
.b-text .hook {{ font-size: 4.2vw; }}
.b-text.big .hook {{ font-size: 5.2vw; }}
</style>"""


def page(slug, c):
    colour, glow = COL[c.get("colour", "gpu")]
    head = f'<!doctype html><meta charset="utf-8">{CSS}<body style="--c:{colour};--glow:{glow}">'
    brand = f'<div class="brand"><img src="{LOGO}" alt="">RigSight</div>'
    label = f'<p class="label">{html.escape(c["label"])}</p>'
    hook = f'<p class="hook">{html.escape(c["hook"])}</p>'
    if c["t"] == "A":
        img = (PHOTOS / f"{c['photo']}.jpg").as_uri()
        fit = " contain" if c.get("fit") == "contain" else ""
        return (head + f'<div class="photo{fit}" style="--img:url(\'{img}\')"></div><div class="fade"></div>{brand}'
                f'<div class="a-text"><span class="bar"></span>{label}{hook}</div>')
    img = (DOCS / "img" / f"{c['shot']}.webp").as_uri()
    card = " card" if c["shot"].startswith("cards/") else ""
    num = f'<p class="num">{html.escape(c["num"])}</p>' if c.get("num") else ""
    extra = (f";--zoom:{c['zoom']}" if c.get("zoom") else "") + (f";--pos:{c['pos']}" if c.get("pos") else "")
    big = "" if num else " big"
    return (head + f'<div class="glow"></div><div class="shot{card}" style="--img:url(\'{img}\'){extra}"></div>{brand}'
            f'<div class="b-text{big}">{label}{num}{hook}</div>')


SHOOT = r"""
const pc = require(process.env.NM + '/puppeteer-core');
const jobs = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));
(async () => {
  const b = await pc.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: 'new', args: ['--allow-file-access-from-files'] });
  const p = await b.newPage();
  for (const [htmlFile, png, w, h] of jobs) {
    await p.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
    await p.goto('file:///' + htmlFile.replace(/\\/g, '/'), { waitUntil: 'load' });
    await p.evaluate(() => document.fonts.ready);
    await p.screenshot({ path: png });
  }
  await b.close();
})();
"""


def main():
    spec = json.loads((HERE / "covers2.json").read_text(encoding="utf-8"))
    spec.pop("_", None)
    only = set(os.environ.get("ONLY", "").split(",")) - {""}
    if only:
        spec = {k: v for k, v in spec.items() if k in only}
    out = Path(os.environ.get("OUT") or (DOCS / "img" / "blog"))
    out.mkdir(parents=True, exist_ok=True)
    tmp = Path(tempfile.gettempdir()) / "rigsight-covers2"
    tmp.mkdir(exist_ok=True)
    jobs = []
    for slug, c in spec.items():
        f = tmp / f"{slug}.html"
        f.write_text(page(slug, c), encoding="utf-8")
        jobs.append([str(f), str(tmp / f"{slug}.png"), 1200, 675])
        jobs.append([str(f), str(tmp / f"{slug}-og.png"), 1200, 630])
    (tmp / "jobs.json").write_text(json.dumps(jobs), encoding="utf-8")
    (tmp / "shoot.cjs").write_text(SHOOT, encoding="utf-8")
    subprocess.run(["node", str(tmp / "shoot.cjs"), str(tmp / "jobs.json")], check=True, env=os.environ)
    for slug in spec:
        im = Image.open(tmp / f"{slug}.png").convert("RGB")
        im.save(out / f"{slug}.webp", "WEBP", quality=84, method=6)
        im.resize((600, 338), Image.LANCZOS).save(out / f"{slug}-600.webp", "WEBP", quality=82, method=6)
        Image.open(tmp / f"{slug}-og.png").convert("RGB").save(out / f"{slug}-og.jpg", "JPEG", quality=84, optimize=True, progressive=True)
    print(f"{len(spec)} covers -> {out}")


if __name__ == "__main__":
    main()
