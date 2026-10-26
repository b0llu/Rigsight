"""Guides: search-targeted how-tos built into docs/guides/ (see GUIDES.md). Run through build.py, which imports this."""
import html, json, re, shutil, sys
from pathlib import Path

GUIDES = Path(__file__).resolve().parent / "guides"
# The order topics are listed in; a guide's "topic" outside this list goes last under its own name.
TOPICS = ["Crashes", "Drivers and updates", "Temperatures", "FPS and games", "Fans", "Storage and memory",
          "Internet and screen time", "Tools"]
TOPIC_OF = {  # writers' own words for a topic -> the list above
    "crash": "Crashes", "blue screen": "Crashes", "restart": "Crashes", "shut": "Crashes",
    "driver": "Drivers and updates", "update": "Drivers and updates", "power plan": "Drivers and updates", "windows": "Drivers and updates",
    "temp": "Temperatures", "heat": "Temperatures", "overheat": "Temperatures",
    "fps": "FPS and games", "game": "FPS and games", "frame": "FPS and games", "overlay": "FPS and games",
    "fan": "Fans", "storage": "Storage and memory", "ssd": "Storage and memory", "drive": "Storage and memory",
    "memory": "Storage and memory", "ram": "Storage and memory", "disk": "Storage and memory",
    "internet": "Internet and screen time", "network": "Internet and screen time", "screen time": "Internet and screen time",
    "data": "Internet and screen time", "tool": "Tools", "software": "Tools", "monitor": "Tools",
}


def B():
    return sys.modules["__main__"]


def topic_of(t):
    if t in TOPICS:
        return t
    low = (t or "").lower()
    for k, v in TOPIC_OF.items():
        if k in low:
            return v
    return t or "Tools"


def load_guides():
    out = []
    if not GUIDES.exists():
        return out
    for f in sorted(GUIDES.glob("*.html")):
        text = f.read_text(encoding="utf-8")
        m = re.match(r"\s*<!--meta\s*(\{.*?\})\s*-->\s*(.*)", text, re.S)
        if not m:
            sys.exit(f"guides/{f.name}: no meta header")
        g = json.loads(m.group(1))
        g["body"] = m.group(2).strip()
        g["topic"] = topic_of(g.get("topic", ""))
        words = len(re.findall(r"\w+", re.sub(r"<[^>]+>", " ", g["body"])))
        g["minutes"], g["words"] = max(2, round(words / 220)), words
        g.setdefault("updated", g["published"])
        g["cover_alt"] = g.get("cover_alt") or (". ".join(f"{k}: {v}, {s}" for k, v, s in g["cover_tiles"]) if g.get("cover_tiles") else g["title"])
        g["cover_cap"] = ""
        if g.get("cover_img"):
            mm = re.search(r"\{\{fig " + re.escape(g["cover_img"]) + r" \|[^|}]*\|([^}]*)\}\}\s*", g["body"])
            if mm:
                g["cover_cap"] = mm.group(1).strip()
                g["body"] = g["body"][:mm.start()] + g["body"][mm.end():]
        out.append(g)
    order = {t: i for i, t in enumerate(TOPICS)}
    out.sort(key=lambda g: (order.get(g["topic"], 99), g["title"].lower()))
    B().ALL_SLUGS.update(g["slug"] for g in out)
    return out


def href(g, mode):
    return f"#{g['slug']}" if mode == "artifact" else g["slug"]


def guides_index_html(guides, mode, img):
    topics = [t for t in TOPICS if any(g["topic"] == t for g in guides)] + sorted({g["topic"] for g in guides} - set(TOPICS))
    chips = "".join(f'<button class="chip" type="button" data-series="{html.escape(t)}" aria-pressed="false">{t} <span>{sum(1 for g in guides if g["topic"] == t)}</span></button>'
                    for t in topics)
    rows = []
    for n, g in enumerate(guides):
        rows.append(f'''<li class="row" data-series="{html.escape(g['topic'])}">
  <a href="{href(g, mode)}">
    <div class="thumb">{B().cover_img(g, img, "", eager=n == 0)}</div>
    <div class="row-text">
      <p class="eyebrow"><span class="dot s-guide"></span>{g['topic']}</p>
      <h2>{g['title']}</h2>
      <p class="sum">{g['summary']}</p>
      <p class="meta">{g['minutes']} min read</p>
    </div>
  </a>
</li>''')
    log = "#all" if mode == "artifact" else "../blog/"
    return f'''<section class="blog-head wrap">
  <p class="kicker">RigSight guides</p>
  <h1>PC guides</h1>
  <p class="intro">Plain answers to the PC problems people search for: why it crashed, how hot is too hot, where your FPS went. Every fix works without RigSight. Where RigSight saves the digging, each guide shows how.</p>
  <p class="starts">How RigSight itself gets built is in <a href="{log}">The RigSight log</a>.</p>
</section>
<section class="wrap">
  <div class="filter" role="group" aria-label="Show guides about">
    <button class="chip" type="button" data-series="all" aria-pressed="true">All <span>{len(guides)}</span></button>{chips}
  </div>
  <ol class="rows" id="rows">
    {''.join(rows)}
  </ol>
</section>'''


def faq_html(g):
    if not g.get("faq"):
        return ""
    items = "".join(f'<details class="qa"><summary>{html.escape(q)}</summary><p>{a}</p></details>' for q, a in g["faq"])
    return f'<section class="faq"><h2>Questions people ask</h2>{items}</section>'


def related(g, guides, n=4):
    same = [x for x in guides if x is not g and x["topic"] == g["topic"]]
    rest = [x for x in guides if x is not g and x["topic"] != g["topic"]]
    return (same + rest)[:n]


def guide_html(g, guides, mode, img):
    home = "#all" if mode == "artifact" else "../"
    index = "#guides" if mode == "artifact" else "./"
    rel = "".join(f'<a class="more" href="{href(x, mode)}"><span>{x["topic"]}</span><b>{x["title"]}</b></a>' for x in related(g, guides))
    cap = f"<figcaption>{g['cover_cap']}</figcaption>" if g["cover_cap"] else ""
    upd = B().nice_date(g["updated"])
    return f'''<article class="post wrap-narrow guide">
  <nav class="crumbs" aria-label="Breadcrumb"><a href="{home}">Home</a><span>/</span><a href="{index}">Guides</a><span>/</span><span>{g['topic']}</span></nav>
  <p class="eyebrow"><span class="dot s-guide"></span>{g['topic']}</p>
  <h1>{g['title']}</h1>
  <p class="dek">{g['summary']}</p>
  <p class="byline"><b>{B().AUTHOR}</b> · Updated {upd} · {g['minutes']} min read</p>
  <figure class="cover">{B().cover_img(g, img, "", eager=True, sizes="(max-width: 760px) 92vw, 728px")}{cap}</figure>
  <div class="short"><p class="short-k">The short answer</p><p>{g['short']}</p></div>
  <div class="body">
{B().expand(g['body'], img, mode == "artifact", [x['slug'] for x in guides], mode)}
  {faq_html(g)}
  </div>
  <nav class="next rel" aria-label="Related guides">{rel}</nav>
  <aside class="get">
    <img src="{img}logo-144.webp" alt="" width="40" height="40">
    <p><b>RigSight is free and open source.</b> It records your PC's temperatures, crashes, app time and internet use all day, and keeps them as history on your PC.</p>
    <a class="btn" href="https://github.com/b0llu/Rigsight/releases/latest">Download for Windows</a>
  </aside>
</article>'''


def live_only(guides):
    """The site gets only the guides released in release.json; links to the others become plain text."""
    released = B().RELEASED
    live = {g["slug"] for g in guides} & set(released)
    for g in guides:
        if g["slug"] in released:
            g["published"] = released[g["slug"]]
            g["updated"] = max(g.get("updated", g["published"]), g["published"])
    held = {g["slug"] for g in guides} - live
    kept = [g for g in guides if g["slug"] in live]
    for g in kept:
        g["body"] = re.sub(r'<a href="([a-z0-9-]+)">(.*?)</a>', lambda m: m.group(2) if m.group(1) in held else m.group(0), g["body"], flags=re.S)
        g["faq"] = [[q, re.sub(r'<a href="([a-z0-9-]+)">(.*?)</a>', lambda m: m.group(2) if m.group(1) in held else m.group(0), a)] for q, a in g.get("faq", [])]
    B().ALL_SLUGS.difference_update(held)
    return kept, held


def build_guides():
    b = B()
    guides, held = live_only(load_guides())
    out = b.DOCS / "guides"
    if out.exists():
        shutil.rmtree(out)
    if not guides:
        print("no guides yet")
        return
    out.mkdir()
    img = "../img/"
    links = dict(home="../", blog="../blog/", guides="./", img=img, on_blog="", on_guides=' class="on"')
    pages = [("", "PC guides: fix crashes, heat, FPS drops and more · RigSight",
              "Plain answers to common PC problems: why your PC crashed, normal GPU and CPU temperatures, FPS drops, loud fans, full drives. Every fix works without extra software.",
              guides_index_html(guides, "site", img), None)]
    for g in guides:
        pages.append((g["slug"], f"{g['title']} · RigSight", g["summary"], guide_html(g, guides, "site", img), g))
    for slug, title, desc, inner, g in pages:
        url = f"{b.SITE}/guides/{slug}"
        crumbs = [{"@type": "ListItem", "position": 1, "name": "Home", "item": f"{b.SITE}/"},
                  {"@type": "ListItem", "position": 2, "name": "Guides", "item": f"{b.SITE}/guides/"}]
        if g:
            crumbs.append({"@type": "ListItem", "position": 3, "name": g["title"], "item": url})
            ld = [{"@context": "https://schema.org", "@type": "Article", "headline": g["title"], "description": g["summary"],
                   "datePublished": g["published"], "dateModified": g["updated"], "image": f"{b.SITE}/img/blog/{g['slug']}.webp",
                   "author": {"@type": "Person", "name": b.AUTHOR}, "publisher": {"@type": "Organization", "name": "RigSight", "url": b.SITE},
                   "mainEntityOfPage": url, "keywords": ", ".join([g["keyword"]] + g.get("also", []))},
                  {"@context": "https://schema.org", "@type": "BreadcrumbList", "itemListElement": crumbs}]
            if g.get("faq"):
                ld.append({"@context": "https://schema.org", "@type": "FAQPage", "mainEntity": [
                    {"@type": "Question", "name": q, "acceptedAnswer": {"@type": "Answer", "text": re.sub(r"<[^>]+>", "", a)}} for q, a in g["faq"]]})
            extra = (f'  <meta property="og:image" content="{b.SITE}/img/blog/{g["slug"]}.webp" />\n  <meta property="og:image:width" content="1200" />\n'
                     f'  <meta property="og:image:height" content="800" />\n  <meta property="og:type" content="article" />\n'
                     + "".join(f'  <script type="application/ld+json">{json.dumps(x)}</script>\n' for x in ld))
        else:
            extra = (f'  <meta property="og:image" content="{b.SITE}/img/og.png" />\n  <meta property="og:type" content="website" />\n'
                     f'  <script type="application/ld+json">{json.dumps({"@context": "https://schema.org", "@type": "BreadcrumbList", "itemListElement": crumbs})}</script>\n')
        doc = (b.head(title, desc, url, img, extra) + b.HEADER.format(**links) + f"<main>{inner}</main>"
               + b.FOOTER.format(**links) + b.PROGRESS_JS + b.UMAMI + "\n</body>\n</html>\n")
        (out / (f"{slug}.html" if slug else "index.html")).write_text(doc, encoding="utf-8", newline="\n")
    sm = b.DOCS / "sitemap.xml"
    text = sm.read_text(encoding="utf-8")
    text = re.sub(r"\s*<url><loc>https://rigsight\.app/guides/[^<]*</loc>.*?</url>", "", text, flags=re.S)
    newest = max(g["updated"] for g in guides)
    entries = "".join(f"\n  <url><loc>{b.SITE}/guides/{s}</loc><lastmod>{(g or {}).get('updated', newest)}</lastmod></url>" for s, _, _, _, g in pages)
    sm.write_text(text.replace("</urlset>", entries + "\n</urlset>"), encoding="utf-8", newline="\n")
    print(f"{len(guides)} guides live, {len(held)} held back, {sum(g['words'] for g in guides):,} words -> {out}")
