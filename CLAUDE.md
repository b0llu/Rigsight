# RigSight

## Writing any text people will read

Before writing or changing any text people will read, open and read `WORDING.md` in this repo, every time, even if you think you remember it. That covers app pages, notifications, tooltips, Settings, errors, What's new, exports, the Spanish pack, the site and the README, and the texts in a mock-up or artifact.

- Follow "How RigSight writes" (sections 1 to 8) and the words table. Don't rely on a summary from memory.
- Run the 7-question review in section 9 on every new or changed line before showing it to anyone.
- Use the shared words in `src/Rigsight.Core/Text/Wording.cs` and `NoticeWords.cs` instead of typing them again.
- If a new text needs a rule WORDING.md doesn't have, ask before you invent one. Once it's agreed, add it to WORDING.md so the file stays the only source.
- `tests/Rigsight.Tests/Core/WordingTests.cs` enforces the retired forms; a new retired form goes there too.
