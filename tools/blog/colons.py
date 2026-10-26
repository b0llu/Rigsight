"""Lists colons used as punctuation in posts and guides (titles, summaries, short answers, FAQ and body text).
Not counted: clock times (10:06), drive paths (C:\\), URLs, code, and HTML. The blog uses no prose colons (the user, 2026-10-26)."""
import json, re, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent


def prose(text):
    text = re.sub(r"<(code|pre|svg|script|style)\b.*?</\1>", " ", text, flags=re.S)
    text = re.sub(r"\{\{fig [^}]*\}\}", " ", text)
    text = re.sub(r"<[^>]+>", " ", text)
    text = re.sub(r"https?://\S+", " ", text)
    text = re.sub(r"\b\d{1,2}:\d{2}\b", " ", text)          # times
    text = re.sub(r"\b[A-Z]:[\\/]", " ", text)               # C:\  D:/
    text = re.sub(r"\b[a-z]{2,}:[a-z]", " ", text)           # sc-domain:x, scheme:
    return text


def hits(f):
    m = re.match(r"\s*<!--meta\s*(\{.*?\})\s*-->\s*(.*)", f.read_text(encoding="utf-8"), re.S)
    meta, body = json.loads(m.group(1)), m.group(2)
    out = []
    fields = [("title", meta["title"]), ("summary", meta["summary"]), ("short", meta["short"])]
    fields += [(f"faq q{i}", q) for i, (q, a) in enumerate(meta.get("faq", []), 1)] + [(f"faq a{i}", a) for i, (q, a) in enumerate(meta.get("faq", []), 1)]
    fields += [("body", body)]
    fields += [("caption", cap) for cap in re.findall(r"\{\{fig [^|]*\|[^|]*\|([^}]*)\}\}", body)]
    fields += [("cover tile", " ".join(t)) for t in meta.get("cover_tiles", [])]
    for where, t in fields:
        for mm in re.finditer(":", prose(t)):
            p = prose(t)
            out.append((where, p[max(0, mm.start() - 50):mm.end() + 30].replace("\n", " ")))
    return out


if __name__ == "__main__":
    total = 0
    for f in sorted((HERE / "guides").glob("*.html")) + sorted((HERE / "posts").glob("*.html")):
        h = hits(f)
        total += len(h)
        if h and "-v" in sys.argv:
            for w, ctx in h:
                print(f"{f.name} [{w}] ...{ctx}...")
        elif h:
            print(f"{len(h):3}  {f.parent.name}/{f.name}")
    print(total, "prose colons")
