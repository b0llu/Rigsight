<p align="center">
  <img src="assets/Rigsight.png" width="96" alt="Rigsight logo" />
</p>

<h1 align="center">Rigsight</h1>

<p align="center">
  <b>Know your rig.</b><br />
  Hardware and usage metrics for Windows, recorded all day and kept as history.
</p>

<p align="center">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" />
  <img alt="CPU cost" src="https://img.shields.io/badge/background%20CPU-~0.01%25-2ea043" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-blue" />
</p>

---

Rigsight records your PC's metrics: temperatures, loads, clocks, power, fans and voltages, memory, drive space and health, which app was in front and for how long, and every crash Windows logs.

Most hardware monitors only show numbers while you're watching. Rigsight keeps them, minute by minute, so you can look back at any day, week, month or year:

- the peak GPU temperature in each game, and when it happened;
- CPU and GPU temperatures at idle and under load, compared with the week before;
- active time per app and per day, against your daily average;
- every crash, grouped, with the temperatures just before it and any driver installed in the days before.

The background agent that records all of this uses about **0.01% of your CPU**.

## Contents

- [Features](#features)
- [Install](#install)
- [Privacy](#privacy)
- [How it stays light](#how-it-stays-light)
- [Building from source](#building-from-source)
- [Making the installer](#making-the-installer)
- [Project layout](#project-layout)
- [FAQ](#faq)
- [Credits](#credits)

## Features

### 🌡️ Hardware
- **Temperatures**: live CPU and GPU gauges, hot spot and memory junction, per-core load, fans, motherboard and drive temperatures, each with today's lowest and highest (tracked all day, even with the window closed), and a history chart for the last 5 minutes to 24 hours or any single day (today, yesterday, or pick a date); hover it for the exact time and temperatures.
- **Memory**: RAM use, live per-app memory (like Task Manager's "private working set"), and today's biggest memory users.
- **Storage**: drive usage and growth, drive health (SSD wear, and a CrystalDiskInfo-style Good/Caution/Bad check with bad-sector counts for hard drives), cleanup suggestions, and a folder scanner with a treemap.
- **All sensors**: every sensor LibreHardwareMonitor can see, searchable, in collapsible groups, with rename and hide.

### 📅 History and reports
- **Home**: today so far (active time, peak temperatures, most-used apps), highlights, and yesterday's totals.
- **One period picker on every page that looks back**: **Day · Week · Month · Year · All time**, with ‹ › to step to the period before or after and a picker for the exact day, month or year. Reports, Apps and Crashes use the same one, so "last month" means the same dates everywhere.
- **Reports** for any day, week, month or year:
  - a minute-by-minute timeline of the app in front, with CPU and GPU temperature drawn over it (a week or month shows a bar per day, a year a bar per month);
  - time per app, split into *in use*, *in the background* and *minimized*;
  - peak CPU and GPU temperature, hot spot, voltage and power, with the time and the app in front;
  - your longest sessions (a minute or more; quick switches still count towards app time);
  - highlights, shown only when there's something to report: time against your daily average, the longest stretch without a break, temperatures compared at the same load (idle against idle, heavy load against heavy load), minutes at your alert limit, and a GPU hot spot running far above the core (a sign the thermal paste needs redoing).
- **One history, all kept for the same time**: minute-by-minute temperatures, app time, sessions and crashes all go back equally far, so every page and every period covers the same dates. Choose how far back it goes, from 3 months to forever; a year takes about 25 MB.
- **Active time, not just time open**: time counts as active only while you're using the app; a fullscreen game counts even when you're not touching the mouse. Time away from the PC is counted separately.

### 🧩 Apps
- Per app, for any day, week, month, year or all time: active, background and minimized time, average and peak CPU/GPU temperature while it was in front, peak memory, average CPU use, sessions, and a chart of its time across the period (per hour for a day, per day for a week or month, per month for a year).
- Rename apps, change their category (game, browser, work…), or exclude them from tracking.

### 💥 Crashes
- App and game crashes, freezes, graphics driver resets, blue screens and sudden shutdowns, all read from Windows' own records.
- **Plain-language explanations**: blue screen codes and faulting modules are translated into what probably happened and what to try.
- Shows what was going on just before each crash: CPU and GPU temperatures, and which game you were in and for how long.
- **List** (every crash, newest at the top, more loading as you scroll) or **Grouped**: the same crash repeated is one row with a count ("Wallpaper Engine crashed ×36"), and several things failing within minutes is one incident ("Your PC froze: 5 apps stopped responding").
- **A timeline** of problems per day, coloured by severity, with the days a driver or Windows update was installed marked, and **"what changed before"**: a blue screen that started two days after a graphics driver install says so.
- **Copy report** (a ready-to-paste summary with your CPU, GPU and driver, RAM and Windows version), **Search online**, and **Show dump file** for blue screens.
- **Mute** an app you don't care about: its crashes leave the totals, timeline, reports and notifications (nothing is deleted).
- Any day (pick it from a calendar, or click a day on the timeline), week, month or year, or everything (including what Windows logged before Rigsight was installed).
- **Patterns**, such as "3 of 4 shutdowns happened while asleep" or "the graphics driver was involved in 5 crashes".
- Filter by **Apps & games** or **PC problems**.

### 🧱 Dashboards
- **Build your own dashboards** from tiles: gauges, a temperature chart, any single sensor, fans, drives, top memory users, today's totals, most used apps, highlights, yesterday and crashes.
- **Drag tiles anywhere, and resize them by dragging their edges.** The others slide out of the way and fill the gaps.
- **Make as many as you like**, such as "Gaming" or "Work", and pick one to open Rigsight on. Each is saved automatically.

### 🎮 Game overlay
- **Press `Alt+Shift+O` in any game** to show or hide a compact readout: FPS, frame time and 1% lows, CPU and GPU temperature, load, clock and power, hot spot, video memory, RAM, the game you're playing and for how long, and the time.
- **Pick exactly what it shows**, which corner it sits in, one row per part or a single line, its size, and the opacity of its background and its readings separately (down to no panel at all, with a soft shadow keeping the numbers readable). Change the shortcut to anything you like.
- **Add any of your sensors** (up to 10): a case fan, a pump, a voltage, a drive or motherboard temperature, anything on All sensors, each with a short name if you like. They stay live in games, even with Rigsight's window closed.
- **Never gets in the way**: it never takes focus, and clicks pass straight through it.
- **Works in every game, exclusive fullscreen included**, through [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) (free, the engine behind MSI Afterburner's overlay). Rigsight hands it the readings and RivaTuner draws them inside the game. The installer offers to install RivaTuner, and Rigsight keeps it running. Without it, the overlay still shows over borderless and windowed games.
- **Safe with anti-cheat**: Rigsight itself never hooks into games. Its own overlay is a normal always-on-top window, and RivaTuner is a long-established tool that anti-cheat systems accept.

### 🖥️ On your desktop
- **Six widgets**: Compact, Slim bar, Gauges, Now playing, Today, Temperature graph. Each has a dark, light or system theme, sizes, separate background and content opacity (down to just the numbers on your wallpaper), and a click-through lock.
- **Tray icon** with a health dot (green, amber or red) and a live readout on hover.
- **Calm notifications**: a daily recap, game session summaries, temperature alerts that ignore brief spikes, and (optionally) a plain-language note when something crashes. Non-urgent cards wait until you leave a fullscreen game.

### 🎛️ Your control
- Dozens of settings: what gets tracked, how long history is kept (3 months, 1 year, 2 years or forever), alert thresholds, units, a black or white app theme (or follow Windows), widget looks, and start with Windows.
- Pause tracking at any time from the tray.
- Clear your history with one click.
- **Updates itself**: new versions download in the background and install the next time you start your PC, or right away with one click on *Restart*. Prefer to decide yourself? Turn automatic updates off and Rigsight just tells you when one is out.

## Install

**One command.** Open PowerShell (press Start, type *PowerShell*) and paste:

```powershell
irm https://b0llu.github.io/Rigsight/install.ps1 | iex
```

It downloads the latest setup from the [Releases](../../releases) page, checks it against GitHub's checksum and opens it. Run it again any time to update: it always opens the newest version's setup. Installing this way doesn't bring up the "Windows protected your PC" screen. ([What the script does](docs/install.ps1).)

**Or download it:**

1. Download **`Rigsight-Setup-x.y.z.exe`** from the [Releases](../../releases) page.
2. Run it. Windows may show *"Windows protected your PC"* because the installer isn't code-signed. Click **More info → Run anyway**.
3. Accept the one admin prompt. The installer sets up the background agent to start with Windows (you can turn this off in Settings).
4. Leave **Install the PawnIO driver** and **Install RivaTuner Statistics Server** ticked if they're offered. PawnIO is needed for CPU and motherboard sensors, RivaTuner for the overlay in exclusive-fullscreen games. Both can take a minute or more to download.

### Updating

Rigsight keeps itself up to date. With **Settings → Updates → Update automatically** on (the default), a new version downloads in the background and installs the next time Windows starts. You can also install it straight away from the app.

**What you see**

| Situation | In the app |
|---|---|
| A new version is out, automatic updates on | It downloads by itself (progress in the sidebar), then an **Update ready** card asks **Restart** or **Later**. |
| A new version is out, automatic updates off | A quiet **Update available** line in the sidebar; click it to download. The agent also shows one notification per version if the window is closed. |
| You chose *Later* | A **Restart to update** line stays in the sidebar. The *Update ready* card comes back once a day, and with automatic updates the next Windows start installs it without asking. |
| The download fails or doesn't match | A background download just tries again later. One you started shows **Update didn't finish** with **Try again** and **Open download page** (the website's installer). |
| The installer ran but this copy is still old | The same card, *"The update didn't install"*. An automatic install is tried **once per version**, so a broken update can't loop at every boot. |

**How it works**

- **Checking.** The agent runs `Rigsight.Agent.exe --update`, a short-lived helper process, 90 seconds after it starts and then every six hours, so the always-running agent never keeps an HTTP stack in memory. The window also checks when it opens. Both share one answer, cached for the day in `%LocalAppData%\Rigsight\updates\latest.json`, so GitHub's [latest-release API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release) is asked at most once a day (plus **Check for updates** in Settings). Offline, nothing is shown.
- **Downloading.** The installer is streamed into `%LocalAppData%\Rigsight\updates`, hashed as it arrives, and kept only if its size and SHA-256 match the `digest` GitHub publishes for the asset. The app and the helper each write their own `.part` file, so they never corrupt each other's download. While the window is open, the helper leaves the download to the app, which shows its progress.
- **Installing.** *Restart* hands the file to the agent, which already has admin rights, so there's no UAC prompt. The installer runs with `/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART /RELAUNCH`: nothing on screen, your previous choices kept (install folder, shortcuts, PawnIO, RivaTuner), then the agent starts again and `/RELAUNCH` reopens the window through Explorer, unelevated. Until the installer closes it, the window stays up with *Updating to x.y.z… Rigsight closes and reopens by itself in a few seconds*, so it never simply vanishes; if it's still there two minutes later, it reports that the update didn't install. At the first check after sign-in, if an update downloaded earlier is still waiting and the window is closed, the helper installs it the same way, without `/RELAUNCH`. Before starting any installer, `updates\attempt.json` records the version, which is how a failed install is noticed and not retried.
- **Trust.** Any program running as you can talk to the agent's pipe, so an install request is treated as untrusted. The agent copies the file into `update\` next to itself in Program Files (writable only by administrators), fetches the latest release from GitHub itself, and runs the copy only if it is byte for byte that release's installer. The worst a rogue request can achieve is installing the genuine latest Rigsight. If the agent isn't running as admin, the app starts the installer itself and Windows asks once.

To uninstall, use **Settings → Apps → Rigsight → Uninstall**. This removes the program and its startup task. Your history stays in `%LocalAppData%\Rigsight`; delete that folder too for a clean removal.

## Privacy

Everything stays on your PC, in `%LocalAppData%\Rigsight`. Rigsight has **no telemetry and no account**. Its only network requests are the update check, at most once a day: an anonymous `GET` of GitHub's public latest-release endpoint (the `User-Agent` is `Rigsight/<version>`), and downloading the installer when there's a new version. Nothing about you or your PC is sent.

It records which app is in front and how hard your hardware is working. It **never** records window titles, keystrokes, screenshots or file contents.

## How it stays light

Rigsight is split into two programs:

| | Runs | Does |
|---|---|---|
| **`Rigsight.Agent.exe`** | Always, from sign-in, with admin rights | Reads sensors, notices which app is in front, writes one small summary per minute to a local SQLite database, and draws the tray icon, widgets, game overlay and notifications. |
| **`Rigsight.exe`** | Only while you have the window open | The app window. It needs no admin rights, reads history from the database, streams live data from the agent over a named pipe, and **fully exits when closed**. |

### Measured resource use

Release build on a Ryzen 7 5700X3D (16 threads, 96 MB L3) with an RTX 3080 Ti and 202 sensors. CPU is the share of the whole PC; memory is private working set, the "Memory" column in Task Manager.

| | CPU | Memory |
|---|---|---|
| **Background agent, window closed** (the normal all-day state) | **~0.006%** | **~40 MB** |
| Background agent with the game overlay showing | ~0.02% | ~40 MB |
| Background agent while the window is open (reads every sensor each second) | ~0.13–0.18% | ~55 MB |
| Rigsight window, on its first page | ~0.07–0.09% | ~45 MB |
| Rigsight window, after opening every page | | ~75 MB (pages stay loaded, so switching back is instant) |

Close the window and its memory is released completely; the agent drops back to the first row.

Where the agent's ~40 MB goes:

| | Private working set |
|---|---|
| NVIDIA's NVML (`nvml.dll` from the driver store), loaded and initialised by LibreHardwareMonitor for GPU power and PCIe throughput | ~18 MB |
| Everything else: the .NET runtime, compiled code, LibreHardwareMonitor, WinForms/GDI+, SQLite, thread stacks | ~18 MB |
| of which the managed (GC) heap | ~1.5 MB |

NVML's cost comes from `nvmlInit`, not from loading the DLL (measured in isolation: +0.4 MB for `LoadLibrary`, +19 MB for `nvmlInit`, none of it returned by `nvmlShutdown`). It's the price of reading an NVIDIA card's power in watts, which NVIDIA's documented NVAPI doesn't provide; on AMD and Intel GPUs it isn't loaded at all.

### How the agent is kept small

- **Tiered sensors**: fast sensors like temperatures and load are read every second, slow ones like drive health every few minutes, and static ones once. While the window is closed, only what the widgets, tray and overlay need is read (`SensorHost.Watch` keeps a watched sensor's hardware updating while it's on screen).
- **An NVIDIA fast path**: GPU temperature, load, power, clocks and memory come from direct NVML calls instead of LibreHardwareMonitor's full `Update()` of the GPU, which is about 70× more expensive.
- **One system call for every process**: per-app CPU and memory come from a single `NtQuerySystemInformation(SystemProcessInformation)` call per sample, not per-process handles and queries.
- **No UI framework in the background**: widgets, the overlay, tray and notifications are drawn with GDI+ into layered windows (`UpdateLayeredWindow` with per-pixel alpha). Background and content opacity are composited into the bitmap, so the window itself stays fully opaque.
- **Garbage collector**: workstation, non-concurrent (no background GC thread), `System.GC.ConserveMemory=7`. The managed heap stays around 1.5 MB.
- **Compiled ahead of time**: published with `PublishReadyToRun` and `TieredCompilation=false`, so code is neither JIT-compiled at startup nor recompiled at tier 1 later. Measured: 22 → 18 MB of its own memory and about 35% less CPU than tiered JIT.
- **Below-normal priority**, and nothing kept in memory that the database already has (the app reads history from SQLite directly).

### How the window is kept small

- **A right-sized gen0 budget.** The workstation GC sizes its gen0 allocation budget from the CPU's last-level cache; on a 96 MB L3 that's ~40 MB of mostly-empty gen0 committed for a UI that allocates a few hundred KB a second. The budget can only be set before the runtime starts (`GCgen0size` isn't honoured from `runtimeconfig.json`), so `Program.Main` starts the app again with `DOTNET_GCgen0size=0x400000` (4 MB) and the child clears the variable so nothing it launches inherits it. Measured: 123 → 73 MB after opening every page; the extra process start costs about 0.1 s. (Server GC with DATAS was tried and used more.)
- **Live history that grows as it's used.** Every sensor keeps the last hour at 1 Hz for sparklines and charts. The ring buffer starts at 64 samples and doubles up to 3,600, stores values as `float` and times as 32-bit millisecond offsets from a base (re-based every ~12 days): 8 bytes a sample instead of 16, and nothing reserved for sensors whose history hasn't filled. The old fixed buffers cost ~12 MB up front for 202 sensors.
- **Only what's on screen is built**: the Apps and Memory lists are virtualized, the Crashes list loads 50 cards at a time as you scroll (infinite scroll), and long tables on Reports start short with "Show more".
- **ReadyToRun, tiered compilation off** as for the agent: about half the CPU over a session, at the cost of about 0.1 s of startup.

### History at scale

Everything (minute readings, hourly per-app totals, sessions, crashes, drive fill) is kept for the same period, set in Settings: 3 months, 1 year, 2 years or forever. Measured with generated history, a typical PC (on ~7 hours a day) uses about **25 MB a year**, a PC on all day about **70 MB a year**.

With two years of heavy synthetic history (656k minute rows, 295k app-hours, 218k sessions, 2k crashes) every page loads in well under a second:

| Query | Time |
|---|---|
| A day, week or month report | 50–240 ms |
| A year's report (and the year before, to compare) | ~60 ms typical, ~200 ms heavy (~260 ms with five years) |
| Apps, a year / all time (300 apps) | ~75 ms / ~170 ms (~380 ms with five years) |
| Crashes, a year / all time, with each crash's context | ~40 ms / ~45 ms (~100 ms with five years) |
| Temperature history for any day | ~8 ms |

What makes that possible:

- **Bounded session lookups**: sessions have no length limit, so "sessions overlapping a range" can't use the start index alone. The agent records the longest session ever saved (`meta.max_session_sec`) and queries bound `start >= from - longest`.
- **A monthly rollup** (`app_month`), updated in the same transaction as `app_hour`: long ranges sum whole months from the rollup and only the partial months at either end from the hourly rows. Month keys are computed by SQLite itself, so writes, reads and pruning always agree; pruning rebuilds the boundary month from the hours that remain.
- **A daily rollup** (`system_day`): each local day's minutes added up (minutes on, active and away time, sums and counts for every average, and the day's highs). Each time the agent writes a minute it recomputes that day's row from the day's own minutes (at most 1,440, by the primary key), so the row is always exact, even if a minute is written twice. A year's report reads 365 rows instead of ~325,000 minutes (1.6 s → ~200 ms on the heavy history) and gives the same totals, averages and peaks to the last digit, checked against the minute-by-minute build on 1, 2 and 5 years of generated history. A peak's time and app come from one indexed lookup in that peak's day. The first start of an agent with this table fills it from existing minutes (0.4 s for a typical history, ~3 s for five heavy years), and pruning rebuilds the boundary day like the monthly rollup.
- **One query for every crash's context** (the app in front, the temperatures in the five minutes before, and the session it ended) instead of three per crash; indexes on `crashes(ts)` and `sessions(app_id, start)`.

## Building from source

**You need:**
- Windows 10/11 x64
- the [.NET 10 SDK](https://dotnet.microsoft.com/download): `winget install Microsoft.DotNet.SDK.10`
- the PawnIO driver, for CPU and motherboard sensors: `winget install namazso.PawnIO`
- (optional) [VS Code](https://code.visualstudio.com/) with the **C# Dev Kit** extension

**From VS Code:**
1. Open the folder and install **C# Dev Kit** when prompted.
2. **Terminal → Run Task… → run Rigsight (agent + app)** builds everything and starts both programs.
3. Before rebuilding, run **Terminal → Run Task… → quit agent**. Windows won't replace a running `.exe`.
4. **Debugging**: press `F5` and pick *Rigsight app* or *Rigsight agent (no admin)*. The debug agent runs without admin rights (so no CPU temperatures) and logs a per-minute CPU profile to `rigsight.log`.

**From a terminal:**
```powershell
dotnet build Rigsight.slnx
.\bin\Debug\Rigsight.Agent.exe   # asks for admin once
.\bin\Debug\Rigsight.exe
```

Both programs build into the same `bin\<Configuration>\` folder so each can find the other.

## Making the installer

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php), which is free.

```powershell
winget install JRSoftware.InnoSetup                                   # once
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

Or in VS Code: **Terminal → Run Task… → build installer**.

This produces `dist\Rigsight-Setup-<version>.exe`. The installer:
- is about 50 MB and self-contained (both programs published ReadyToRun for win-x64), so the target PC doesn't need .NET;
- installs to Program Files and adds Start menu and (optional) desktop shortcuts;
- offers to install PawnIO and RivaTuner Statistics Server;
- stops a running copy before updating;
- removes the startup task when uninstalled.

The version number comes from `<Version>` in `Directory.Build.props`.

**Testing an update end to end.** Released builds only ever take updates from GitHub. To try the whole flow against a local server, build two *test* installers with `$env:RigsightTestFeed = 'true'` set (for example 0.5.0 and 0.5.1, changing `<Version>` in between), install the older one, and serve the newer one from a fake "latest release" endpoint (JSON with `tag_name` and an asset carrying `name`, `size`, `browser_download_url` and `digest: "sha256:..."`). Then point Rigsight at it with `setx RIGSIGHT_UPDATE_FEED http://127.0.0.1:8765/latest`; the agent started by Task Scheduler picks it up from your user environment. Debug builds accept the same variable. Afterwards, remove the variable, delete `%LocalAppData%\Rigsight\updates` and reinstall a normal build.

To publish a release:
```powershell
gh release create vX.Y.Z dist\Rigsight-Setup-X.Y.Z.exe --title "Rigsight X.Y.Z" --notes-file notes.md
```

## Project layout

```
Rigsight.slnx
Directory.Build.props     version + shared build settings (ReadyToRun, tiered compilation off)
src/
  Rigsight.Core/          shared by both programs
    Settings/               settings model + JSON store (the agent is the only writer)
    Protocol/               messages sent between agent and app
    Data/                   SQLite schema, writer and queries
    Reports/                report builder + insight engine
    Stability/              crash log reader + plain-language explainer
    Apps/                   app naming and categories (games, browsers, ...)
    Updates/                latest-release lookup, the shared update folder, verified downloads
  Rigsight.Agent/         background agent (WinForms, no main window)
    Sensors/                LibreHardwareMonitor host, NVIDIA fast path, sensor history
    Tracking/               foreground/idle detection, per-process sampler, sessions
    Widgets/                GDI+ widget renderer and layered windows
    Ui/                     tray icon, menus, notification cards
    Ipc/                    named-pipe server
    UpdateInstaller.cs      background updater (--update) and verified installs with the agent's admin rights
  Rigsight/               the app window (WPF, Fluent, dark and light themes, MVVM)
    Program.cs              entry point (sets the GC's gen0 budget, then starts the app)
    Views/ ViewModels/ Controls/ Services/ Themes/
installer/Rigsight.iss    Inno Setup script
tools/                    build-installer.ps1, make-icon.ps1
assets/                   logo and icon
docs/                     the website (GitHub Pages) and install.ps1, the one-command installer
```

## FAQ

**Why does it need admin rights?**
Windows only lets programs with admin rights read CPU and motherboard sensors. Only the small background agent runs as admin, and it's started by a Task Scheduler task, so you get one UAC prompt ever instead of one at every boot. The app window runs as a normal user.

**My CPU temperature is missing.**
Install the PawnIO driver (`winget install namazso.PawnIO`) and restart Rigsight. Also check the agent is running as admin: the sidebar shows a warning if it isn't.

**Does it slow down games?**
No. The agent uses about 0.01% of total CPU (0.02% with the overlay showing) and runs at below-normal priority. Non-urgent notifications also wait until you leave fullscreen.

**The overlay doesn't show up over my game.**
The game is probably in *exclusive* fullscreen, which hides every ordinary window. Check the Overlay page: RivaTuner should be installed and running (the page can install it for you), and a game that was already open when RivaTuner started needs a restart. Or switch the game to borderless (sometimes called "fullscreen windowed"). The page also warns you if another program already uses the shortcut.

**Does it work with AMD (or Intel) graphics cards and CPUs?**
Yes. All hardware is read through LibreHardwareMonitor, which supports NVIDIA, AMD and Intel GPUs and Intel and AMD CPUs. NVIDIA cards also get an extra fast path (NVML/NVAPI) because NVIDIA's full driver query is unusually expensive. Other cards use the standard route, which is already light.

**Where is my data?**
In `%LocalAppData%\Rigsight`: `settings.json`, the `rigsight.db` SQLite database, a small log, and `updates\` (the day's version check, a downloaded update and the last install attempt; old installers are removed).

## Credits

- Hardware readings: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and the [PawnIO](https://pawnio.eu) driver
- MVVM: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- Storage: [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore)
- Installer: [Inno Setup](https://jrsoftware.org/isinfo.php)
- In-game overlay in fullscreen games: [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) by Unwinder (optional, installed separately)

## License

[MIT](LICENSE)
