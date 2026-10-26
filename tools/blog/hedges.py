"""Compares hedge words in each post and guide against a git revision (default HEAD), so an unslop pass can't silently
turn a softened, unconfirmed claim into a certain one. Prints every sentence that lost a hedge.
  python tools/blog/hedges.py [rev]"""
import re, subprocess, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
HEDGES = r"\b(may|might|can|could|usually|often|typically|most likely|likely|probably|about|around|roughly|some|in most cases|many|most|tends? to|sometimes|reportedly)\b"


def sentences(text):
    text = re.sub(r"<[^>]+>", " ", text)
    return [s.strip() for s in re.split(r"(?<=[.!?])\s+", re.sub(r"\s+", " ", text)) if s.strip()]


def key(s):
    return set(re.findall(r"[a-z0-9°%]{4,}", s.lower())) - {"rigsight"}


rev = sys.argv[1] if len(sys.argv) > 1 else "HEAD"
lost = 0
for f in sorted((HERE / "guides").glob("*.html")) + sorted((HERE / "posts").glob("*.html")):
    rel = f.relative_to(ROOT).as_posix()
    try:
        old = subprocess.run(["git", "show", f"{rev}:{rel}"], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", check=True).stdout
    except subprocess.CalledProcessError:
        continue
    new_s = sentences(f.read_text(encoding="utf-8"))
    for s in sentences(old):
        hs = {h.lower() for h in re.findall(HEDGES, s, re.I)}
        if not hs:
            continue
        k = key(s)
        if len(k) < 4:
            continue
        # the sentence in the new text that shares the most words with this one
        best = max(new_s, key=lambda n: len(k & key(n)), default="")
        if len(k & key(best)) < max(4, len(k) // 2):
            continue  # rewritten beyond matching; read by hand if in doubt
        gone = hs - {h.lower() for h in re.findall(HEDGES, best, re.I)}
        if gone:
            lost += 1
            print(f"{f.name}: lost {sorted(gone)}\n   was: {s[:220]}\n   now: {best[:220]}\n")
print(lost, "sentences lost a hedge")
