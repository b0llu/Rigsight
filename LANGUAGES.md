# Languages

RigSight is written in English. Other languages come as **language packs**: one plain-text file per language, downloaded only by someone who picks that language in Settings. With no pack, the app is exactly the English it always was.

## How it works

- `Rigsight.Core/Text/Lang.cs` gives every text the app shows in the language in use. **The English text is the key.**
  - Code: `Lang.T("Crashes")`, `Lang.T($"{app} crashed at {time}")`.
  - Pages: `Text="{ctl:T Crashes}"`, quoted when the text has a comma: `Text="{ctl:T 'Average, today'}"`.
- An interpolated text is looked up by its **template**, holes numbered in order: `$"{app} crashed at {time:t}"` is the entry `"{0} crashed at {1:t}"`. The pack may put the holes in any order, but must use each one and no others; an entry that doesn't fit is dropped and that text stays English.
- Anything the pack lacks stays English, so an older pack still works with a newer app.
- Numbers, dates and times keep following Windows' region settings, as before. Only the words change.
- Packs live in `languages\<code>.json` in the repo and are released as `Rigsight-Language-<code>.json` on each GitHub release. The app downloads the one for its own version, checks GitHub's SHA-256, and keeps it in `%LocalAppData%\Rigsight\languages`.

## Writing text that can be translated

1. **A whole sentence is one text.** Never build a sentence from translated pieces (`T("Your") + " " + T("CPU")`): other languages order and agree their words differently. Put the whole sentence in one `Lang.T($"...")` with the values as holes.
2. **Counts get a text for one and one for many**, each whole: `n == 1 ? Lang.T("1 app crash") : Lang.T($"{n} app crashes")`. Never `$"{n} crash{(n == 1 ? "" : "es")}"`.
3. **A hole holds a value, not words of the sentence**: a name, a number, a time, or a phrase that is itself translated where it's made (a cause like "a power cut", a part like "GPU"). A hole must never be an English word picked by a condition: `$"{(hotter ? "hotter" : "cooler")}"` becomes two whole sentences.
4. **Formats stay in the hole** (`{temp:0.0}`), so the template carries them; the translator keeps them as they are.
5. **Keep a text's English exactly where it was.** The English output with no pack must not change by a single character: the tests check English everywhere.
6. **Words kept in a table** (a list of labels, a `switch` of names) are translated where they're shown with `Lang.T(label)`, and marked where they're written with `Lang.Key("...")` so the language tool finds them. A `const string` that's shown becomes `static string X => Lang.T("...")`.
7. **Never translate what isn't shown to people**: log lines (logs stay English so they can be read), settings keys and values, file names, pipe messages, database text, sensor and hardware names from the drivers, app and game names, process names, URLs, command-line switches, text compared in code (`if (label == "Today")`; compare the underlying value instead, or translate only where it's shown), and anything read back in.
8. **Product and part names stay as they are**: RigSight, CPU, GPU, RAM, NVMe, RivaTuner, Steam, Windows. They're written inside the sentence, not as holes.
9. **Text only for the test copy, the developer or the log** stays English.
10. **The problem report** that people copy for support stays English (it's read by the developer).

## Keeping a pack up to date

The English is the key, so **rewording English leaves that sentence untranslated** until the pack is updated. Nothing breaks, but it shows in English. So:

- `dotnet run --project tools\Rigsight.LangTool -- sync languages\es.json` adds every new text as an empty entry and moves entries whose English is gone to `"stale"`, keeping their old translation next to the new English to make rewording a quick edit.
- `... -- check languages\es.json` lists every text not translated or that doesn't fit. **A test fails while that list isn't empty**, so a release can't ship with untranslated text.
- `... -- release languages\es.json <version> <out>` writes the release asset.

## Spanish

Neutral Spanish, `tú` form, short and plain like the English (see WORDING.md for the ideas each word stands for). The words for the same idea are the same on every page, as in English.
