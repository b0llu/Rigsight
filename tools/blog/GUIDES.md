# How to write a RigSight guide

Guides live in `tools/blog/guides/<slug>.html` and are built into rigsight.app/guides/. A guide answers one thing
people type into Google ("how to find out why pc crashed windows 11"). It must fully solve the problem for someone who
never installs RigSight, and then show plainly where RigSight makes it easier. Help first, product second.

## File format

```
<!--meta
{"slug": "find-out-why-pc-crashed", "series": "guide", "published": "2026-10-26", "updated": "2026-10-26",
 "topic": "Crashes",
 "keyword": "how to find out why pc crashed",
 "also": ["why did my pc crash", "event viewer crash logs windows 11", "pc keeps crashing"],
 "title": "How to find out why your PC crashed (Windows 11 and 10)",
 "summary": "One or two sentences for the guide list and search results, with the main keyword in it.",
 "short": "THE SHORT ANSWER, 40 to 60 words. Answers the search directly, like a featured snippet: what it is, what to do first.",
 "faq": [["Question people ask?", "Two to four sentence answer."], ["...", "..."]],
 "cover_img": "pages/crashes", "cover_crop": [263, 118, 1131, 754], "cover_alt": "What the cover shows, in words",
 "app": ["Crashes page (0.24.0)", "Timeline (0.15.0)"],
 "sources": ["https://learn.microsoft.com/...", "https://..."]}
-->
<p>Body HTML starts here.</p>
```

- `slug`: the main keyword in lower case with dashes, short. No dates or versions.
- `title`: the main keyword near the start, natural English, under 65 characters if you can. Add "(Windows 11)" etc. only when people search for it.
- `keyword` / `also`: from `%LOCALAPPDATA%\RigsightKeywords\suggest.json` (Google autocomplete). Use the exact phrasing people type, in the title, the first paragraph, one heading and the FAQ. Each phrase once or twice, naturally. Never stuff.
- `faq`: 3 to 5 real questions from the autocomplete list ("is 85 degrees hot for a gpu while gaming"), answered short. They become FAQ rich results.
- `cover_img` + `cover_crop`: a crop [x, y, w, h] of a picture in `docs/img/pages/` (all 1424 x 924; the app's sidebar is x < 263). Crop to the card that matters for this guide, ratio near 3:2. Use only pictures that exist. If none fits, omit cover_img and the builder draws a tile cover from `cover_tiles`: [["LABEL", "value", "what the value is"]].
- `app`: every RigSight feature the guide mentions, with the version it shipped in, CHECKED in the code (see Accuracy).
- `sources`: the pages you checked the fix advice against (Microsoft, NVIDIA, AMD, Intel docs first; well-known sites second).
- Body pictures: `{{fig pages/NAME | alt text | caption}}` uses a picture from docs/img/pages. At most two in a guide.

## Shape of the body (about 1,000 to 1,600 words)

1. One short opening paragraph that names the problem the way the reader would, with the main keyword. No throat-clearing.
2. `<h2>` sections that solve it, in the order a person should try things. Numbered steps (`<ol>`) for anything they click through in Windows, with the exact names of the menus and buttons ("Event Viewer > Windows Logs > System").
3. What the readings or messages mean, with real numbers (safe and unsafe ranges, what an error code means). Say where a number comes from when it varies by maker.
4. One `<h2>` titled like "How RigSight makes this easier" (vary the wording): what RigSight shows for this exact problem, using its real page names, with one picture. Two to four short paragraphs. RigSight is free and open source; say so once. It never replaces the steps above, it saves the digging and keeps the history so the reader can see what happened before they started looking.
5. A closing paragraph with the next step to take, not a summary.

## Voice

Write like a person who builds PCs explaining it to a friend. Plain words, short sentences, one idea each. "You" for the reader.
No em dashes or en dashes, no hyphen used as a dash, no parentheses for asides. No "not just X, but Y". No groups of three for
rhythm. No slogans or aphorisms. No AI words (crucial, delve, enhance, leverage, robust, seamless, landscape, testament,
navigate, unlock, elevate). No "In this guide we will". No "Conclusion" heading. No emoji. Bold only for the name of a button or menu.

## Accuracy (most important)

- RigSight facts: check every feature against the RELEASED code in `C:\Users\saman\AppData\Local\RigsightWork\blog\src`
  (this is version 0.34.0, the newest release) and its What's new text (`src/Rigsight/**/WhatsNew*`, `ReleaseNotes*`,
  or grep the release feature list). If you can't find it in the code, don't claim it.
- NOT released, never mention: the AI helper / Ask RigSight / Explain buttons, per-frame frame times, stutter detection,
  the "Every frame" chart, reading a game's own settings files ("How it's set"). The older removed helper Riggy is history only.
- RivaTuner Statistics Server is needed for FPS readings and for the overlay inside exclusive fullscreen games; say so where FPS comes up.
- Some readings need RigSight to run with admin rights (CPU temperature and power, motherboard fans, drive health, per-app internet). Mention it when the guide depends on one.
- Fix advice: check it against official docs with web search before writing it. Prefer safe steps. Never tell people to flash a
  BIOS, change voltages or edit the registry without a clear warning; prefer the Windows Settings route. Temperature limits: give the
  maker's numbers (e.g. NVIDIA, AMD and Intel max temps) and say they differ by model.
- Never invent statistics, studies, user stories or quotes. No "we tested 50 PCs".
- No promises about future features or dates.
- Pictures show the site's showcase PC (made-up data); describe them as "RigSight's Crashes page", never as someone's real PC.
