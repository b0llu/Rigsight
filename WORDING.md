# Wording

One idea, one wording, on every page, notification and export. New text follows this before it ships.

The shared words live in one place, `src/Rigsight.Core/Text/Wording.cs`. Use them from there rather than typing them again.

No colon inside a line people read (since 0.35.0): lead with the fact, join with a comma, "with", "because" or "since", or make it two sentences. "2 graphics driver resets since driver 621.01, none in the 14 days before", not "Driver 621.01: more graphics problems: 2 graphics driver resets". Clock times, short label and value readouts in a chart's hover, and text only the AI reads keep theirs.

## How RigSight writes

Write as a knowledgeable person explaining something about someone's PC. Not a monitoring dashboard, a technical manual, a marketing department or an AI chatbot. Be natural, be precise, be useful, then stop writing.

### 1. Like a human, not a manual

Natural, conversational English. Nothing robotic, overly formal, generic or more technical than it needs to be.

| Good | Not |
|---|---|
| Your connection was restored after 50 seconds of downtime. | Internet connectivity has successfully resumed following a network interruption lasting 50 seconds. |
| Your Wi-Fi disconnected 3 times in the last 10 minutes. | Multiple wireless connectivity interruptions have been detected within the recent monitoring interval. |

### 2. Short, but not robotic

Shorter isn't automatically better; the goal is natural brevity, not the fewest words.

- Remove unneeded words, not useful context.
- Don't repeat what a title already said.
- One well-written sentence beats several fragments.
- Don't squeeze a sentence that reads better a little longer.
- Never add filler to make a description feel complete.

### 3. Lead with what matters

Copy answers the most relevant of: what happened, what it means, why should I care, what (if anything) to do next. Not every line needs all four.

In a notification the title says what happened; the description adds the most useful supporting fact.

| Title | Good description | Not |
|---|---|---|
| Your internet is down | Your router is reachable, but there's no internet connection. | Your PC is still connected to your router, but no internet is coming through it. This is usually your internet provider. |

The good one is direct and doesn't guess at the cause.

### 4. Precise without overexplaining

Accuracy matters more than sounding authoritative.

- An observation is never turned into a diagnosis.
- Correlation is never said as causation.
- Consequences are never exaggerated.
- No invented technical explanations or recommendations.
- The right terms and units; measurements, thresholds and timeframes kept exactly.
- What Windows reports, what RigSight observes and what RigSight infers are kept apart.

| Good | Not |
|---|---|
| Cyberpunk 2077 runs 7°C hotter since driver 616.92, compared with the 10 days before the update. | Driver 616.92 is overheating your GPU. |
| Your SSD has only 4% available spare capacity remaining. | Your SSD is almost out of storage. |

The last pair are different technical conditions; never swap one for the other.

### 5. Calm, not alarming

RigSight should be trustworthy, not frightening. No "Your PC is in serious danger!", "This could destroy your hardware!", "Your fan is cooking your CPU!", "Your drive is about to die!", "You must fix this immediately!". Say the actual risk calmly: "A stopped CPU fan can cause temperatures to rise quickly." / "SSD failures can happen with little warning. Backing up your data helps protect against data loss." Urgent words only when the evidence really warrants them.

### 6. No generic AI or marketing writing

None of "It's important to note that…", "In order to ensure…", "Seamlessly monitor…", "Unlock powerful insights…", "Take control of your PC…", "Experience the next generation of…", "Leverage advanced capabilities…", "Comprehensive and actionable insights…", "Designed to empower users…". Simple words that say the actual benefit; ordinary features aren't made to sound revolutionary.

### 7. Every notification earns its place

- Urgent: genuinely serious situations only.
- Important: meaningful changes.
- Quiet: useful information that doesn't interrupt.
- Nothing that only repeats what is already shown elsewhere.
- Measurements, durations, comparisons and history when they add value; clarity over sounding interesting.

The user should learn something useful every time RigSight interrupts them.

### 8. One voice

Knowledgeable but approachable, technical but understandable, confident but never overconfident, helpful but never preachy, informative but never verbose. The same terms, capitals, units and formatting everywhere (the table below).

### 9. Review before it ships

Read every new or changed line against these, and rewrite it if any answer is wrong:

1. Would a real person naturally say this?
2. Is the meaning clear at once?
3. Can words go without losing anything useful?
4. Does it repeat what the title or the screen around it already says?
5. Is every technical claim backed by real data?
6. Does it explain the problem without exaggerating it?
7. Does it sound like RigSight, not generic AI-written software?

## The words

| Idea | Say it like this | Not |
|---|---|---|
| How sure | Chips: **CONFIRMED**, **LIKELY &lt;CAUSE&gt;**, **POSSIBLY &lt;CAUSE&gt;**, **NOT ENOUGH TO SAY**. The chip is the same in a list and on the opened crash. | "POSSIBLE: …", a chip without its cause |
| What a big change did | **Before and after** (the filter, the opened page). A result only when something got better or worse: "Crimson Desert runs 6° hotter on the GPU since this driver". Chips **LIKELY THE DRIVER** / **POSSIBLY THE UPDATE** / **POSSIBLY ONE OF THESE** (two big changes on one day); the causes are THE DRIVER, THE UPDATE, THE BIOS, THE NEW WINDOWS, THE NEW HARDWARE, THE POWER PLAN. "Since" while the change is still the current one, "in the N days after" once another big change came. Nothing at all when nothing moved. | "As before", "No change", "not enough to say yet" lines under a change; a bare "LIKELY" |
| An app crash | **crashed**: "Dota 2 crashed at 9:14 PM with a .NET error." | "closed unexpectedly", "stopped on an unhandled error", "went down" |
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
| Hotter or cooler than usual | Both figures and the load: "77° against 67–69°, at the same power". What rules the room out is said as the figure that does: "With nothing running it sits at its usual 48°". | "and not because of the room", "Not the room", "stands in for the room" |
| The PC's own problems | **PC problems**: blue screens, graphics driver resets and sudden shut-offs while in use. Likely power cuts are said apart, in grey, and never judged. | counting power cuts or app crashes as PC problems |
| A line on Home | One statement (it may wrap to a second line), nothing under it. Amber only for what needs a look. It opens a page only where that page shows more (`InsightLinks`); a complete fact opens nothing. | a second line of detail, "…" cutting it short, a link on every line |
| Not enough history yet | **Too soon to say**, then what it still needs: "Too soon to say. It needs 4 weeks of games or other heavy use, and has 2 so far." On a tile line: "Too soon to say for Fan #6" (the user, 2026-11-14). | "Not enough data", "Insufficient history", "Collecting data…" |
| A drive whose health can't be read | What RigSight can show and why: "Only how full it is, through this USB adapter" / "Only how full it is, since it's on another computer" (the user, 2026-11-14). | "Health unavailable", "N/A", "Unsupported" |
| A time that isn't exact | The time alone, **7:42 AM**; where there's hover, it says the window: "Between 7:41 AM and 7:42 AM" (the user, 2026-11-13). | "~7:42 AM", "about 7:42 AM" |
| How long | **2h 35m**, **43m**, **40s** (`Units.Duration`). Inside a notification's sentences, in words: "48 minutes into the game", "You played Dota 2 for 2 hours and 14 minutes" (`NoticeWords.Long`); a notification is read away from the app, as a sentence. | "7 h 30 min", "43 min"; "48m" inside a notification's sentence |
| The graphics part | **GPU** in labels and sentences. "graphics driver" stays. | "graphics card", "GPU driver" |
| Internet amounts | **Internet** for amounts used: "Internet 66 GB". **Network** is only the page's name. | "network use", "data" |
| Open the detail | Nothing: a click on any notification opens its page, so no notification says so (the user, 2026-11-05). Only where the click does something people wouldn't expect: "Click to update.", "Click to watch it." | "Click for the full picture.", "Click for the full breakdown." |
| A temperature in a notification | With its unit, in the one picked: **78°C**, **7°C hotter** (°F when picked). A notification is read away from the app, with nothing around it to say the unit. Pages keep **77°**. | "78°" in a notification |

## How sure is about the cause, never the event

That a crash happened is Windows' own record, so the event is always said as a fact. Only its cause can be likely or possible.

- A confirmed cause is plain fact, with no tag: "RigsightTestCrash crashed at 10:58 AM with a .NET error (NullReferenceException)."
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
