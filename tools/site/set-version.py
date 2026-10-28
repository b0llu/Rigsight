# Writes the app's released version into the site's home page: the softwareVersion Google reads, and every
# <span data-version> people see. Run by .github/workflows/site-version.yml when a release is published or edited.
#   python tools/site/set-version.py v0.36.0
import re
import sys
from pathlib import Path

version = sys.argv[1].lstrip("v")
if not re.fullmatch(r"\d+\.\d+\.\d+", version):
    sys.exit(f"Not a version: {sys.argv[1]}")

page = Path(__file__).resolve().parents[2] / "docs" / "index.html"
html = page.read_text(encoding="utf-8")
html, json_count = re.subn(r'"softwareVersion": "[^"]*"', f'"softwareVersion": "{version}"', html)
html, span_count = re.subn(r"<span data-version>[^<]*</span>", f"<span data-version>{version}</span>", html)
if json_count != 1 or span_count == 0:
    sys.exit(f"Expected 1 softwareVersion and some data-version spans, found {json_count} and {span_count}")
page.write_text(html, encoding="utf-8", newline="\n")
print(f"Home page set to {version} ({span_count} visible spots)")
