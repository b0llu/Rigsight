# Wording

One idea, one wording, on every page, notification and export. New text follows this before it ships.

The shared words live in one place, `src/Rigsight.Core/Text/Wording.cs`. Use them from there rather than typing them again.

## The words

| Idea | Say it like this | Not |
|---|---|---|
| How sure | Chips: **CONFIRMED**, **LIKELY &lt;CAUSE&gt;**, **POSSIBLY &lt;CAUSE&gt;**, **NOT ENOUGH TO SAY**. The chip is the same in a list and on the opened crash. | "POSSIBLE: …", a chip without its cause |
| An app crash | **crashed**: "Dota 2 crashed at 9:14 PM: a .NET error." | "closed unexpectedly", "stopped on an unhandled error", "went down" |
| An app that hangs | **froze**: "Chrome froze." Chip **FROZE**. The whole PC: "The PC froze." | "NOT RESPONDING", "stopped responding" |
| A blue screen | **blue screen**: "Windows crashed with a blue screen." Chip **BLUE SCREEN**. | "Windows crashed (blue screen)" |
| The PC going off by itself | **sudden shut-off** (noun), "The PC shut off suddenly" (sentence), chip **SUDDEN SHUT-OFF**. "Lost power" only as a cause: "likely a power cut". | "unexpected shutdown", "sudden shutdown", "POWER LOST", "the PC went down" |
| A graphics driver reset | **graphics driver reset**, chips and tiles too. | "GPU DRIVER RESET", "Driver reset", "Graphics trouble" |
| All of them together | **problems**: "No problems today." | "No crashes or sudden shutdowns" |
| Average, highest, lowest | **Average**, **Highest**, **Lowest** in full. A range: "37° to 55°", with "today" only where the card doesn't already say the period. | AVG, avg, "1-min avg", MIN, MAX |
| A middle figure of a period | **Average**, worked out as an average. | "Typical" |
| A peak | The part first: **CPU peak**, **GPU peak** (CPU PEAK in capitals). | "Peak CPU", "Hottest GPU" |
| Compared with before | **your usual**, only for this PC's own past: "49m less than your usual Wednesday". | "your daily average", "on a usual day" |
| A week back | **the last 7 days**; comparing: **the 7 days before**. | "the week before", "over the previous 7 days" |
| How long | **2h 35m**, **43m**, **40s** (`Units.Duration`). | "7 h 30 min", "43 min" |
| The graphics part | **GPU** in labels and sentences. "graphics driver" stays. | "graphics card", "GPU driver" |
| Internet amounts | **Internet** for amounts used: "Internet 66 GB". **Network** is only the page's name. | "network use", "data" |
| Open the detail | **Click for the full picture.** on every notification. | "Click for the full breakdown." |

## How sure is about the cause, never the event

That a crash happened is Windows' own record, so the event is always said as a fact. Only its cause can be likely or possible.

- A confirmed cause is plain fact, with no tag: "RigsightTestCrash crashed at 10:58 AM: a .NET error (NullReferenceException)."
- Otherwise the chip's word: "Rocket League crashed at 11:07 PM, likely as it was starting." / "…, possibly heat."
- Nothing to go on: "Not enough on record to say why."
- Never "Confirmed by Windows."

A notification's title is the event ("Dota 2 crashed", "Chrome froze", "Sudden shut-off"). It is only sent once Windows' record has been read.

## Nothing to show: three cases

- **Not recorded**: RigSight wasn't recording (the PC was off, the agent stopped).
- **No reading**: it was recording, and the sensor gave nothing.
- **No …**, naming what didn't happen: "No drops", "No problems", "No apps used yet today".

A box that is still loading shows the spinner, never one of these.

## Alignment

Text beside an icon: the icon sits at the top of its row, and the text is centred in the row. One line sits on the icon's middle; several lines start level with the icon's top and continue under each other. The icon never moves down to the middle of a long text.

Every list of icon-and-text rows uses the `IconRow` style in `src/Rigsight/Themes/Theme.xaml` (with `IconBox` for the 30 by 30 square), so no page can drift.

## Sameness

Cards or groups side by side have the same parts in the same order. If one has a bar, a main figure or a footnote, they all do, or none does. A part that has nothing true to show for one of them is a reason to drop it from all of them, not to leave a gap in one.

## What stays as it is

- Windows' own words quoted in a crash's technical details (like "The previous system shutdown was unexpected"): they are Windows' text, and they are what a web search finds.
- Past What's new entries (`Services/Changelog.cs`): they are history.

## How it is enforced

`tests/Rigsight.Tests/Core/WordingTests.cs` reads every string in the app's C# and every piece of text in its XAML (never comments) and fails on a retired form, naming the file and line and what to say instead. Its allowlist holds the two exceptions above; anything else added to it needs a reason in a comment.
