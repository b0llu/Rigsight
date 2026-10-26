"""Checks posts and guides against GUIDES.md's voice and accuracy rules before a build."""
import json, re, sys
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
DOCS = HERE.parents[1] / "docs"
BANNED = ["crucial", "delve", "enhance", "leverage", "robust", "seamless", "landscape", "testament", "unlock", "elevate",
          "not just", "conclusion", "background agent", "two years by default", "kept two years", "game-changer", "ultimate", "boasts", "showcase",
          "ask rigsight", "explain this crash", "every frame chart", "\"every frame\"", "stutter detection", "how it's set", "ai helper", "riggy"]
ALLOWED_IN = {"crucial": ["Crucial"]}  # the SSD brand
files = sorted((HERE / "guides").glob("*.html")) + sorted((HERE / "posts").glob("*.html"))
slugs = set()
metas = {}
for f in files:
    m = re.match(r"\s*<!--meta\s*(\{.*?\})\s*-->\s*(.*)", f.read_text(encoding="utf-8"), re.S)
    meta = json.loads(m.group(1))
    metas[f] = (meta, m.group(2))
    slugs.add(meta["slug"])
problems = 0


def bad(f, msg):
    global problems
    problems += 1
    print(f"{f.parent.name}/{f.name}: {msg}")


for f, (meta, body) in metas.items():
    guide = f.parent.name == "guides"
    text = re.sub(r"<[^>]+>", " ", body) + " " + meta.get("short", "") + " " + meta.get("summary", "") + " " + meta["title"]
    text += " " + " ".join(q + " " + a for q, a in meta.get("faq", []))
    for ch, name in (("—", "em dash"), ("–", "en dash")):
        if ch in text:
            bad(f, f"{name} x{text.count(ch)}")
    if re.search(r"\w \- \w", text):
        bad(f, "hyphen used as a dash: " + re.search(r".{20}\w \- \w.{20}", text).group(0))
    for w in BANNED:
        if not guide and w in ("ai helper", "riggy", "ask rigsight", "explain this crash", "every frame"):
            continue
        for hit in re.finditer(re.escape(w), text, re.I):
            if w in ALLOWED_IN and hit.group(0) in ALLOWED_IN[w]:
                continue
            bad(f, f"'{w}': ...{text[max(0, hit.start() - 50):hit.end() + 40]}...")
    for h in re.findall(r'href="([a-z0-9-]+)"', body):
        if h not in slugs:
            bad(f, f"link to missing slug '{h}'")
    for src in re.findall(r"\{\{fig ([^|}]+)", body):
        if not (DOCS / "img" / f"{src.strip()}.webp").exists():
            bad(f, f"missing picture {src}")
    if guide:
        for k in ("keyword", "short", "faq", "summary", "topic", "sources", "app"):
            if not meta.get(k):
                bad(f, f"meta lacks {k}")
        n = len(meta.get("short", "").split())
        if not 35 <= n <= 65:
            bad(f, f"short answer is {n} words")
        if len(meta["title"]) > 70:
            bad(f, f"title {len(meta['title'])} chars")
        kw = meta["keyword"].lower()
        if not any(w in meta["title"].lower() for w in kw.split()[:3]):
            bad(f, "title lacks the keyword's first words")
        if meta.get("cover_img"):
            w, h = Image.open(DOCS / "img" / f"{meta['cover_img']}.webp").size
            x, y, cw, ch = meta["cover_crop"]
            if x + cw > w or y + ch > h:
                bad(f, f"crop {meta['cover_crop']} outside {w}x{h}")
# no colons as punctuation anywhere in the writing (the user, 2026-10-26); times, paths, URLs and code are fine
import colons
for f in files:
    for where, ctx in colons.hits(f):
        bad(f, f"colon in {where}: ...{ctx.strip()}...")
print(f"{len(files)} files, {problems} problems")
sys.exit(1 if problems else 0)
