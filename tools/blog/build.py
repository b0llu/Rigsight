"""Builds the RigSight blog from tools/blog/posts/*.html.

  python tools/blog/build.py site       -> docs/blog/index.html + docs/blog/<slug>.html, sitemap entries
  python tools/blog/build.py artifact D -> D/index.html (one page, posts behind #slug) + the pictures and fonts it uses

A post file starts with a JSON header in <!--meta ... -->, then its body HTML.
Short codes in a body:
  {{fig pages/crash-open | alt text | caption}}   a picture from docs/img (-720 copy used on phones when it exists)
Review notes for the author: <p class="rv">...</p>. The site build leaves them out and lists them.
"""
import html, json, re, shutil, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs"
POSTS = Path(__file__).resolve().parent / "posts"
SITE = "https://rigsight.app"
AUTHOR = "Dhruv Samant"
AUTHOR_SLUG, METHOD_SLUG = "author", "how-rigsight-measures"  # pages inside the blog, not in its list (2026-11-18)
PAGES = Path(__file__).resolve().parent / "pages"
# What my PC is, for a post's "Test setup" block; a post adds or overrides rows ({{testbed Recorded=... | Counted=...}}).
TESTBED = [("GPU", "NVIDIA GeForce RTX 3080 Ti, 12 GB"), ("CPU", "AMD Ryzen 7 5700X3D"), ("Memory", "16 GB, 32 GB from 14 November 2026"),
           ("Windows", "Windows 11")]
HOSTS = {"learn.microsoft.com": "Microsoft Learn", "support.microsoft.com": "Microsoft Support", "blogs.windows.com": "Windows blog",
         "www.nvidia.com": "NVIDIA", "nvidia.custhelp.com": "NVIDIA support", "www.amd.com": "AMD", "www.intel.com": "Intel",
         "www.tomshardware.com": "Tom's Hardware", "forums.tomshardware.com": "Tom's Hardware forums", "www.pcgamer.com": "PC Gamer",
         "www.pcgameshardware.de": "PC Games Hardware", "www.igorslab.de": "Igor's Lab", "www.techpowerup.com": "TechPowerUp",
         "www.howtogeek.com": "How-To Geek", "www.pcworld.com": "PCWorld", "www.digitalcitizen.life": "Digital Citizen",
         "www.blurbusters.com": "Blur Busters", "crystalmark.info": "CrystalDiskInfo", "help.steampowered.com": "Steam Support",
         "gamesbeat.com": "GamesBeat", "www.tweaktown.com": "TweakTown", "www.rockstargames.com": "Rockstar Games",
         "www.capframex.com": "CapFrameX", "www.elevenforum.com": "Eleven Forum", "www.sandisk.com": "Sandisk", "www.crucial.com": "Crucial", "www.kingston.com": "Kingston", "www.samsung.com": "Samsung", "support.xbox.com": "Xbox Support", "answers.microsoft.com": "Microsoft Community", "steamcommunity.com": "Steam Community", "www.techradar.com": "TechRadar", "www.windowscentral.com": "Windows Central", "www.reddit.com": "Reddit", "superuser.com": "Super User", "www.overclock.net": "Overclock.net", "linustechtips.com": "Linus Tech Tips forum", "www.anandtech.com": "AnandTech", "en.wikipedia.org": "Wikipedia", "www.makeuseof.com": "MakeUseOf", "www.windowsdigitals.com": "Windows Digitals", "pureinfotech.com": "Pureinfotech", "developer.android.com": "Android Developers", "www.msi.com": "MSI"}


def source_label(url):
    """"Microsoft Learn, Get physicaldisk": who published it and which page, from the address alone."""
    from urllib.parse import urlparse, unquote
    u = urlparse(url)
    who = HOSTS.get(u.netloc) or u.netloc.removeprefix("www.")
    generic = {"help", "topic", "t", "faq", "faqs", "blog", "blogs", "news", "article", "articles", "post", "p", "answers",
               "questions", "forum", "forums", "threads", "detail", "a_id", "app", "solutions", "en-us", "en-US", "en", "us", "au",
               "zh", "br", "hub", "games", "bytes", "insights", "reviews", "media", "support", "kb", "resources", "support-articles"}
    segs = [unquote(s) for s in u.path.split("/") if s]
    what = ""
    for s in reversed(segs):
        s = re.sub(r"\.(html?|aspx|php|md)$", "", s)
        s = re.sub(r"-?[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", "", s)   # a trailing GUID
        s = re.sub(r"[.-]\d+$", "", s)                                        # a trailing page id
        if not s or s in generic or len(s) <= 3 or s.lower() == "www" or re.fullmatch(r"[\d_~%-]+|[0-9a-f]{12,}", s):
            continue
        what = re.sub(r"[-_]+", " ", s).strip()
        break
    if not what or what.replace(" ", "").lower() == who.replace(" ", "").lower():
        return who
    return f"{who}, {what[0].upper() + what[1:]}"


def sources_html(urls, mode="site"):
    if not urls:
        return ""
    items = "".join(f'<li><a href="{html.escape(u)}" rel="noopener">{html.escape(source_label(u))}</a></li>' for u in urls)
    me = f'<li><a href="{"#" if mode == "artifact" else ""}{METHOD_SLUG}">How RigSight measures</a>, how the numbers from my PC were recorded</li>'
    return f'<section class="sources"><h2>Sources</h2><ol>{items}{me}</ol></section>'


def author_box(img_prefix, mode="site"):
    pre = "#" if mode == "artifact" else ""
    return (f'<aside class="authorbox"><span class="avatar" aria-hidden="true">DS</span>'
            f"<div><b>{AUTHOR}</b><p>I build RigSight on my own. The numbers in these posts come from my own PC's recorded history, "
            f'checked against the released app. <a href="{pre}{AUTHOR_SLUG}">About me</a> · <a href="{pre}{METHOD_SLUG}">How RigSight measures</a></p></div></aside>')


def byline_name(mode="site"):
    return f'<a href="{"#" if mode == "artifact" else ""}{AUTHOR_SLUG}" rel="author">{AUTHOR}</a>'
SERIES = {
    "build": ("Building RigSight", "What broke, what I got wrong, and how it got fixed."),
    "ai": ("The AI journey", "From a helper I removed to a real one that only says what it can back up."),
    "fix": ("PC problems", "How RigSight catches a PC problem, why it happens, and how to fix it."),
}
RELEASE = json.loads((Path(__file__).resolve().parent / "release.json").read_text(encoding="utf-8"))
RELEASED = RELEASE["released"]  # slug -> date it went live on rigsight.app
MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August",
          "September", "October", "November", "December"]


def load_posts():
    posts = []
    for f in sorted(POSTS.glob("*.html")):
        text = f.read_text(encoding="utf-8")
        m = re.match(r"\s*<!--meta\s*(\{.*?\})\s*-->\s*(.*)", text, re.S)
        if not m:
            sys.exit(f"{f.name}: no meta header")
        meta = json.loads(m.group(1))
        meta["body"] = m.group(2).strip()
        words = len(re.findall(r"\w+", re.sub(r"<[^>]+>", " ", meta["body"])))
        meta["minutes"] = max(2, round(words / 220))
        meta["words"] = words
        posts.append(meta)
    covers = json.loads((Path(__file__).resolve().parent / "covers.json").read_text(encoding="utf-8"))
    for p in posts:
        c = covers.get(p["slug"])
        if not c:
            sys.exit(f"{p['slug']}: no entry in covers.json")
        if "alt" in c:
            p["cover_alt"] = c["alt"]
        elif "tiles" in c:
            p["cover_alt"] = ". ".join(f"{k}: {v}, {s}" for k, v, s in c["tiles"])
        elif "chips" in c:
            p["cover_alt"] = c["chips_label"] + ": " + "; ".join(c["chips"])
        else:
            p["cover_alt"] = c["label"] + ". " + c["sub"]
        p["cover_cap"] = ""
        if "img" in c:
            # the cover is cut from this picture: drop the same picture from the body, keep its caption under the cover
            m = re.search(r"\{\{fig " + re.escape(c["img"]) + r" \|[^|}]*\|([^}]*)\}\}\s*", p["body"])
            if m:
                p["cover_cap"] = m.group(1).strip()
                p["body"] = p["body"][:m.start()] + p["body"][m.end():]
    posts.sort(key=lambda p: (p["happened"], p.get("order", 0)))
    ALL_SLUGS.update(p["slug"] for p in posts)
    for s in SERIES:
        # Counted over released parts only, so a live post never says "part 5 of 6" while part 6 waits in the queue.
        part = [p for p in posts if p["series"] == s]
        live = [p for p in part if p["slug"] in RELEASED]
        held = [p for p in part if p["slug"] not in RELEASED]
        for i, p in enumerate(live, 1):
            p["part"], p["of"] = i, len(live)
        for i, p in enumerate(held, len(live) + 1):
            p["part"], p["of"] = i, len(part)
    return posts


def nice_date(iso):
    y, m, d = (int(x) for x in iso.split("-"))
    return f"{d} {MONTHS[m - 1]} {y}"


def figure(img_prefix, src, alt, cap, eager=False):
    small = DOCS / "img" / f"{src}-720.webp"
    full = f"{img_prefix}{src}.webp"
    from PIL import Image  # noqa: only to read the size so the page doesn't jump
    w, h = Image.open(DOCS / "img" / f"{src}.webp").size
    srcset = f' srcset="{img_prefix}{src}-720.webp 720w, {full} {w}w" sizes="(max-width: 760px) 92vw, 1152px"' if small.exists() else ""
    load = "" if eager else ' loading="lazy"'
    return (f'<figure class="shot"><img src="{full}"{srcset} width="{w}" height="{h}" alt="{html.escape(alt)}"{load} decoding="async">'
            f'<figcaption>{cap}</figcaption></figure>')


def expand(body, img_prefix, keep_review, slugs=(), mode="site"):
    slugs = set(slugs) | ALL_SLUGS
    first = [True]
    if mode == "artifact":
        body = re.sub(r'href="([a-z0-9-]+)"', lambda m: f'href="#{m.group(1)}"' if m.group(1) in slugs else m.group(0), body)

    def fig(m):
        parts = [x.strip() for x in m.group(1).split("|")]
        out = figure(img_prefix, parts[0], parts[1], parts[2] if len(parts) > 2 else "", eager=False)
        first[0] = False
        return out
    body = re.sub(r"\{\{fig (.*?)\}\}", fig, body)

    def webimg(m):
        # A picture from elsewhere on the web, shown from its own address with a credit (the user, 2026-11-18):
        # {{webimg URL | alt | caption | credit name | credit page URL}}
        u, alt, cap, who, page = ([x.strip() for x in m.group(1).split("|")] + [""] * 5)[:5]
        credit = f'Image: <a href="{html.escape(page or u)}" rel="noopener">{html.escape(who)}</a>' if who else ""
        cap = (cap + (" " if cap and credit else "") + credit).strip()
        return (f'<figure class="webimg"><img src="{html.escape(u)}" alt="{html.escape(alt)}" loading="lazy" decoding="async" referrerpolicy="no-referrer">'
                + (f"<figcaption>{cap}</figcaption>" if cap else "") + "</figure>")
    body = re.sub(r"\{\{webimg (.*?)\}\}", webimg, body)

    def testbed(m):
        rows = dict(TESTBED)
        for part in (m.group(1) or "").split("|"):
            if "=" in part:
                k, v = part.split("=", 1)
                rows[k.strip()] = v.strip()
        dl = "".join(f"<dt>{html.escape(k)}</dt><dd>{html.escape(v)}</dd>" for k, v in rows.items())
        return f'<div class="testbed"><p class="testbed-k">Test setup</p><dl>{dl}</dl></div>'
    body = re.sub(r"\{\{testbed ?(.*?)\}\}", testbed, body)
    if not keep_review:
        body = re.sub(r'\s*<(p|div) class="rv">.*?</\1>', "", body, flags=re.S)
    return body


def used_pictures(posts):
    pics = set()
    for p in posts:
        for m in re.finditer(r"\{\{fig (.*?)\}\}", p["body"]):
            src = m.group(1).split("|")[0].strip()
            pics.add(f"{src}.webp")
            if (DOCS / "img" / f"{src}-720.webp").exists():
                pics.add(f"{src}-720.webp")
        pics.add(f"blog/{p['slug']}.webp")
        pics.add(f"blog/{p['slug']}-600.webp")
    return sorted(pics)


def cover_img(p, img_prefix, cls, eager=False, sizes="(max-width: 760px) 92vw, 460px"):
    """Every post has a cover drawn by covers.py: a crop of the app, or tiles that say what their number is."""
    if not (DOCS / "img" / "blog" / f"{p['slug']}.webp").exists():
        sys.exit(f"{p['slug']}: no cover, run covers.py")
    load = 'fetchpriority="high"' if eager else 'loading="lazy"'
    return (f'<img class="{cls}" src="{img_prefix}blog/{p["slug"]}.webp" srcset="{img_prefix}blog/{p["slug"]}-600.webp 600w, '
            f'{img_prefix}blog/{p["slug"]}.webp 1200w" sizes="{sizes}" width="1200" height="800" alt="{html.escape(p["cover_alt"])}" {load} decoding="async">')


ALL_SLUGS = set()  # every post and guide slug, so links between them work in the one-page preview

# ---------- pieces shared by the site and the artifact ----------

CSS = (Path(__file__).resolve().parent / "blog.css").read_text(encoding="utf-8")


def post_href(p, mode):
    return f"#{p['slug']}" if mode == "artifact" else f"{p['slug']}"


def index_html(posts, mode, img_prefix, fixes=()):
    everything = list(posts) + list(fixes)
    # newest first: a story by when it happened, a PC-problem post by when it went out
    newest = sorted(everything, key=lambda p: (p["published"] if p["series"] == "fix" else p["happened"], p.get("order", 0)), reverse=True)
    chips = "".join(
        f'<button class="chip" type="button" data-series="{k}" aria-pressed="false">{v[0]} <span>{sum(1 for p in everything if p["series"] == k)}</span></button>'
        for k, v in SERIES.items() if any(p["series"] == k for p in everything))
    rows = []
    for n, p in enumerate(newest):
        part = f", part {p['part']} of {p['of']}" if p["series"] == "ai" else (f" · {p['topic']}" if p["series"] == "fix" else "")
        when = nice_date(p["published"] if p["series"] == "fix" else p["happened"])
        rows.append(f'''<li class="row" data-series="{p['series']}">
  <a href="{post_href(p, mode)}">
    <div class="thumb">{cover_img(p, img_prefix, "", eager=n == 0)}</div>
    <div class="row-text">
      <p class="eyebrow"><span class="dot s-{p['series']}"></span>{SERIES[p['series']][0]}{part}</p>
      <h2>{p['title']}</h2>
      <p class="sum">{p['summary']}</p>
      <p class="meta">{when} · {p['minutes']} min read</p>
    </div>
  </a>
</li>''')
    first_build = next((p for p in posts if p["series"] == "build"), None)
    first_ai = next((p for p in posts if p["series"] == "ai"), None)
    starts = " or ".join(x for x in [
        f'<a href="{post_href(first_build, mode)}">{first_build["title"]}</a>' if first_build else "",
        f'<a href="{post_href(first_ai, mode)}">part 1 of the AI journey</a>' if first_ai else ""] if x)
    return f'''<section class="blog-head wrap">
  <p class="kicker">RigSight blog</p>
  <h1>The RigSight log</h1>
  <p class="intro">Building a PC monitor that keeps your PC's history. What broke, what got fixed, and the AI helper it is growing into. And the PC problems RigSight catches, why they happen and how to fix them.</p>
  <p class="starts">Start at the beginning with {starts}.</p>
</section>
<section class="wrap">
  <div class="filter" role="group" aria-label="Show posts from">
    <button class="chip" type="button" data-series="all" aria-pressed="true">All <span>{len(everything)}</span></button>{chips}
    {'' if mode == "artifact" else '<a class="rss" href="feed.xml">RSS</a>'}
  </div>
  <ol class="rows" id="rows">
    {''.join(rows)}
  </ol>
</section>'''


def article_html(p, posts, mode, img_prefix, keep_review):
    i = posts.index(p)
    same = [q for q in posts if q["series"] == p["series"]]
    j = same.index(p)
    prev_p = same[j - 1] if j > 0 else None
    next_p = same[j + 1] if j + 1 < len(same) else None
    other = next((q for q in reversed(posts) if q["series"] != p["series"]), None)
    nav = []
    if prev_p:
        nav.append(f'<a class="more" href="{post_href(prev_p, mode)}"><span>Before this</span><b>{prev_p["title"]}</b></a>')
    if next_p:
        nav.append(f'<a class="more" href="{post_href(next_p, mode)}"><span>What happened next</span><b>{next_p["title"]}</b></a>')
    if len(nav) < 2 and other:
        nav.append(f'<a class="more" href="{post_href(other, mode)}"><span>From {SERIES[other["series"]][0]}</span><b>{other["title"]}</b></a>')
    series_line = f'{SERIES[p["series"]][0]}' + (f', part {p["part"]} of {p["of"]}' if p["series"] == "ai" else "")
    back = "#all" if mode == "artifact" else "./"
    home = "#all" if mode == "artifact" else "../"
    return f'''<article class="post wrap-narrow">
  <nav class="crumbs" aria-label="Breadcrumb"><a href="{home}">Home</a><span>/</span><a href="{back}">Blog</a></nav>
  <p class="eyebrow"><span class="dot s-{p['series']}"></span>{series_line}</p>
  <h1>{p['title']}</h1>
  <p class="dek">{p['summary']}</p>
  <p class="byline"><b>{byline_name(mode)}</b> · {nice_date(p['happened'])}{(' · Version ' + p['version']) if p.get('version') else ''} · {p['minutes']} min read</p>
  <figure class="cover">{cover_img(p, img_prefix, "", eager=True, sizes="(max-width: 760px) 92vw, 1152px")}{('<figcaption>' + p['cover_cap'] + '</figcaption>') if p['cover_cap'] else ''}</figure>
  <div class="short"><p class="short-k">The short version</p><p>{p['short']}</p></div>
  <div class="body">
{expand(p['body'], img_prefix, keep_review, [q['slug'] for q in posts], mode)}
  {author_box(img_prefix, mode)}
  </div>
  <nav class="next" aria-label="More posts">{''.join(nav)}</nav>
  <aside class="get">
    <img src="{img_prefix}logo-144.webp" alt="" width="40" height="40">
    <p><b>RigSight is free and open source.</b> It records your PC's temperatures, app time, internet use and crashes, and keeps them as history on your PC.</p>
    <a class="btn" href="https://github.com/b0llu/Rigsight/releases/latest">Download for Windows</a>
  </aside>
</article>'''


def load_pages():
    """The blog's own pages (author, how RigSight measures): written like posts, not listed or dated."""
    out = []
    for f in sorted(PAGES.glob("*.html")):
        m = re.match(r"\s*<!--meta\s*(\{.*?\})\s*-->\s*(.*)", f.read_text(encoding="utf-8"), re.S)
        meta = json.loads(m.group(1))
        meta["body"] = m.group(2)
        out.append(meta)
    ALL_SLUGS.update(x["slug"] for x in out)
    return out


def page_html(pg, mode, img_prefix):
    back = "#all" if mode == "artifact" else "./"
    home = "#all" if mode == "artifact" else "../"
    return f'''<article class="post wrap-narrow page">
  <nav class="crumbs" aria-label="Breadcrumb"><a href="{home}">Home</a><span>/</span><a href="{back}">Blog</a></nav>
  <p class="eyebrow">The RigSight log</p>
  <h1>{pg['title']}</h1>
  <p class="dek">{pg['summary']}</p>
  <div class="body">
{expand(pg['body'], img_prefix, False, [], mode)}
  </div>
</article>'''


HEADER = '''<header class="top"><div class="wrap top-in">
  <a class="brand" href="{home}"><img src="{img}logo-144.webp" alt="" width="24" height="24"><span>RigSight</span></a>
  <nav aria-label="Site"><a href="{blog}" class="on">Blog</a><a href="{home}#faq">FAQ</a><a class="coffee" href="https://buymeacoffee.com/bollu">Buy me a coffee</a></nav>
  <div class="top-end"><a class="icon-btn" href="https://github.com/b0llu/Rigsight" aria-label="RigSight on GitHub"><svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true"><path fill="currentColor" d="M12 .5a11.5 11.5 0 0 0-3.64 22.41c.58.1.79-.25.79-.56v-2c-3.2.7-3.88-1.37-3.88-1.37-.52-1.33-1.28-1.69-1.28-1.69-1.05-.71.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.68 0-1.25.45-2.28 1.19-3.08-.12-.29-.52-1.46.11-3.04 0 0 .97-.31 3.17 1.18a11 11 0 0 1 5.77 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.58.23 2.75.11 3.04.74.8 1.19 1.83 1.19 3.08 0 4.41-2.69 5.39-5.25 5.67.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56A11.5 11.5 0 0 0 12 .5"/></svg></a><a class="btn btn-sm" href="https://github.com/b0llu/Rigsight/releases/latest">Download</a></div>
</div><div class="progress" aria-hidden="true"><i></i></div></header>'''

FOOTER = '''<footer class="foot"><div class="wrap foot-in">
  <a class="brand" href="{home}"><img src="{img}logo-144.webp" alt="" width="20" height="20"><span>RigSight</span></a>
  <nav aria-label="Links"><a href="{home}">Home</a><a href="{blog}">Blog</a><a href="https://github.com/b0llu/Rigsight">GitHub</a><a href="https://github.com/b0llu/Rigsight/releases">Releases</a><a href="{home}privacy.html">Privacy policy</a><a class="coffee" href="https://buymeacoffee.com/bollu">Buy me a coffee</a></nav>
</div></footer>'''

PROGRESS_JS = '''<script>
(function () {
  var bar = document.querySelector('.progress i');
  if (!bar) return;
  function tick() {
    var post = [].find.call(document.querySelectorAll('.post'), function (a) { return a.offsetParent !== null; });
    if (!post) { bar.style.transform = 'scaleX(0)'; return; }
    var r = post.getBoundingClientRect(), h = r.height - innerHeight;
    var f = h > 0 ? Math.min(1, Math.max(0, -r.top / h)) : 1;
    bar.style.transform = 'scaleX(' + f.toFixed(3) + ')';
  }
  addEventListener('scroll', tick, { passive: true });
  addEventListener('resize', tick);
  window.__tick = tick; tick();
})();
(function () {
  var chips = document.querySelectorAll('.chip[data-series]');
  if (!chips.length) return;
  function show(s) {
    chips.forEach(function (c) { c.setAttribute('aria-pressed', c.dataset.series === s ? 'true' : 'false'); });
    document.querySelectorAll('#rows .row').forEach(function (r) { r.hidden = s !== 'all' && r.dataset.series !== s; });
  }
  chips.forEach(function (c) { c.addEventListener('click', function () { show(c.dataset.series); }); });
})();
</script>'''


def head(title, desc, url, img, extra=""):
    return f'''<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
  <title>{html.escape(title)}</title>
  <meta name="description" content="{html.escape(desc)}" />
  <meta name="theme-color" content="#000000" />
  <meta name="color-scheme" content="dark" />
  <link rel="canonical" href="{url}" />
  <link rel="icon" href="/favicon.ico" sizes="48x48" />
  <link rel="icon" type="image/png" sizes="192x192" href="{img}icon-192.png" />
  <meta property="og:site_name" content="RigSight" />
  <meta property="og:title" content="{html.escape(title)}" />
  <meta property="og:description" content="{html.escape(desc)}" />
  <meta property="og:url" content="{url}" />
  <meta name="twitter:card" content="summary_large_image" />
  <link rel="alternate" type="application/rss+xml" title="The RigSight log" href="{SITE}/blog/feed.xml" />
  <link rel="preload" href="../fonts/geist-latin.woff2" as="font" type="font/woff2" crossorigin />
{extra}  <style>
@font-face {{ font-family: "Geist"; font-style: normal; font-weight: 300 700; font-display: swap; src: url("../fonts/geist-latin.woff2") format("woff2"); }}
@font-face {{ font-family: "Geist Mono"; font-style: normal; font-weight: 400 500; font-display: swap; src: url("../fonts/geist-mono-latin.woff2") format("woff2"); }}
{CSS}
  </style>
</head>
<body>
'''


UMAMI = '''<script defer src="https://cloud.umami.is/script.js" data-website-id="d4359802-485d-4a07-97c4-6a8083d4a95d"></script>'''


def live_posts(posts):
    """Only released posts go on the site, dated by their release; links to held posts become plain text."""
    # Held guides too: a post can link to a guide that is still queued.
    guide_slugs = {m.group(1) for f in (POSTS.parent / "guides").glob("*.html")
                   if (m := re.search(r'"slug":\s*"([a-z0-9-]+)"', f.read_text(encoding="utf-8")))}
    held = ({p["slug"] for p in posts} | guide_slugs) - set(RELEASED)
    kept = [p for p in posts if p["slug"] in RELEASED]
    for p in kept:
        p["published"] = RELEASED[p["slug"]]
        p["body"] = re.sub(r'<a href="([a-z0-9-]+)">(.*?)</a>', lambda m: m.group(2) if m.group(1) in held else m.group(0), p["body"], flags=re.S)
    ALL_SLUGS.difference_update(held)
    return kept


def build_site():
    posts = live_posts(load_posts())
    fixes, _held = live_only(load_guides())
    out = DOCS / "blog"
    if out.exists():
        shutil.rmtree(out)
    out.mkdir()
    img = "../img/"
    links = dict(home="../", blog="./", img=img)
    pages = [("", "Blog · RigSight, a free PC monitor for Windows",
              "How RigSight, a free PC monitor for Windows, gets made, and the PC problems it catches, why they happen and how to fix them.",
              index_html(posts, "site", img, fixes), None)]
    for p in posts:
        pages.append((p["slug"], f"{p['title']} · The RigSight log", p.get("description") or p["summary"], article_html(p, posts, "site", img, False), p))
    for pg in load_pages():
        pages.append((pg["slug"], f"{pg['title']} · The RigSight log", pg["description"], page_html(pg, "site", img), None))
    for slug, title, desc, inner, p in pages:
        url = f"{SITE}/blog/{slug}"
        extra = ""
        if p:
            ld = {"@context": "https://schema.org", "@type": "BlogPosting", "headline": p["title"], "description": p.get("description") or p["summary"],
                  "datePublished": p["published"], "author": {"@type": "Person", "name": AUTHOR},
                  "publisher": {"@type": "Organization", "name": "RigSight", "url": SITE}, "mainEntityOfPage": url}
            ld["image"] = f"{SITE}/img/blog/{p['slug']}.webp"
            ld["author"]["name"] = AUTHOR
            extra += f'  <meta property="og:image" content="{SITE}/img/blog/{p["slug"]}.webp" />\n  <meta property="og:image:width" content="1200" />\n  <meta property="og:image:height" content="800" />\n'
            extra += '  <meta property="og:type" content="article" />\n'
            extra += f'  <script type="application/ld+json">{json.dumps(ld)}</script>\n'
        else:
            extra += f'  <meta property="og:image" content="{SITE}/img/og.png" />\n  <meta property="og:type" content="website" />\n'
        doc = (head(title, desc, url, img, extra) + HEADER.format(**links) + f"<main>{inner}</main>"
               + FOOTER.format(**links) + PROGRESS_JS + UMAMI + "\n</body>\n</html>\n")
        (out / (f"{slug}.html" if slug else "index.html")).write_text(doc, encoding="utf-8", newline="\n")
    # RSS, newest first
    from email.utils import format_datetime
    from datetime import datetime, timezone
    items = "".join(
        f"<item><title>{html.escape(p['title'])}</title><link>{SITE}/blog/{p['slug']}</link><guid>{SITE}/blog/{p['slug']}</guid>"
        f"<pubDate>{format_datetime(datetime.fromisoformat(p['happened']).replace(hour=12, tzinfo=timezone.utc))}</pubDate>"
        f"<description>{html.escape(p['summary'])}</description></item>"
        for p in reversed(posts))
    (out / "feed.xml").write_text(
        f'<?xml version="1.0" encoding="UTF-8"?>\n<rss version="2.0"><channel><title>The RigSight log</title><link>{SITE}/blog/</link>'
        f"<description>Notes from building RigSight, a free PC monitor for Windows.</description><language>en</language>{items}</channel></rss>\n",
        encoding="utf-8", newline="\n")
    # sitemap: the home page, privacy and every blog page
    sm = DOCS / "sitemap.xml"
    text = sm.read_text(encoding="utf-8")
    text = re.sub(r"\s*<url><loc>https://rigsight\.app/blog/[^<]*</loc>.*?</url>", "", text, flags=re.S)
    entries = "".join(f"\n  <url><loc>{SITE}/blog/{s}</loc><lastmod>{(p or {}).get('published', posts[-1]['published'])}</lastmod></url>"
                      for s, _, _, _, p in pages)
    text = text.replace("</urlset>", entries.lstrip("\n").join(["", "\n</urlset>"]) if False else entries + "\n</urlset>")
    sm.write_text(text, encoding="utf-8", newline="\n")
    review = [(p["slug"], re.sub(r"<[^>]+>", "", m)) for p in posts for m in re.findall(r'<p class="rv">(.*?)</p>', p["body"], re.S)]
    words = sum(p["words"] for p in posts)
    print(f"{len(posts)} posts live, {words:,} words -> {out}")
    for s, t in review:
        print(f"  review note left out of {s}: {t.strip()[:110]}")


def build_artifact(dest):
    posts = load_posts()
    dest = Path(dest)
    if dest.exists():
        shutil.rmtree(dest)
    (dest / "img").mkdir(parents=True)
    (dest / "fonts").mkdir()
    img = "img/"
    guides = load_guides()
    for pic in sorted(set(used_pictures(posts) + used_pictures(guides))) + ["logo-144.webp"]:
        (dest / "img" / pic).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(DOCS / "img" / pic, dest / "img" / pic)
    for f in ("geist-latin.woff2", "geist-mono-latin.woff2"):
        shutil.copy(DOCS / "fonts" / f, dest / "fonts" / f)
    links = dict(home="#all", blog="#all", img=img)
    head_bits = '''<title>The RigSight Log</title>
<meta name="claude:viewport-fit-sides" content="cover">
<style>
@font-face { font-family: "Geist"; font-style: normal; font-weight: 300 700; font-display: swap; src: url("fonts/geist-latin.woff2") format("woff2"); }
@font-face { font-family: "Geist Mono"; font-style: normal; font-weight: 400 500; font-display: swap; src: url("fonts/geist-mono-latin.woff2") format("woff2"); }
''' + CSS + '''
.rv { border: 1px dashed #facc15; background: rgba(250,204,21,.08); color: #fde68a !important; border-radius: 10px; padding: 10px 14px; font-size: 14px !important; }
.rv::before { content: "Note for you, left out of the site: "; font-weight: 600; }
</style>
'''
    views = [f'<div class="view" id="v-all">{index_html(posts, "artifact", img, guides)}</div>']
    for p in posts:
        views.append(f'<div class="view" id="v-{p["slug"]}" hidden>{article_html(p, posts, "artifact", img, True)}</div>')
    if guides:
        for g in guides:
            views.append(f'<div class="view" id="v-{g["slug"]}" hidden>{guide_html(g, guides, "artifact", img)}</div>')
    router = '''<script>
(function () {
  function go() {
    var id = (location.hash || '#all').slice(1);
    var el = document.getElementById('v-' + id) || document.getElementById('v-all');
    document.querySelectorAll('.view').forEach(function (v) { v.hidden = v !== el; });
    el.querySelectorAll('.post').forEach(function (a) { a.hidden = false; });
    scrollTo(0, 0);
    if (window.__tick) window.__tick();
  }
  addEventListener('hashchange', go);
  go();
})();
</script>'''
    page = (head_bits + HEADER.format(**links) + "<main>" + "\n".join(views) + "</main>" + FOOTER.format(**links)
            + PROGRESS_JS + router)
    (dest / "index.html").write_text(page, encoding="utf-8", newline="\n")
    files = sorted(str(f.relative_to(dest)).replace("\\", "/") for f in dest.rglob("*") if f.is_file() and f.name != "index.html")
    (dest / "files.json").write_text(json.dumps(files), encoding="utf-8")
    print(f"{len(posts)} posts + {len(guides)} guides, {sum(p['words'] for p in posts + guides):,} words, {len(files) - 1} files -> {dest}")
    for p in posts:
        print(f"  {p['happened']}  {p['series']:5} {p['minutes']:2} min  {p['slug']}")


from guides import load_guides, guide_html, build_guides, live_only  # noqa: E402

if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "site":
        build_site()
        build_guides()
    elif len(sys.argv) >= 3 and sys.argv[1] == "artifact":
        build_artifact(sys.argv[2])
    else:
        sys.exit(__doc__)
