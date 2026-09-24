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

Most hardware monitors only show numbers while you're watching. Rigsight keeps them, minute by minute, so you can look back at any day, week or month:

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
- **Reports** for any day, week or month (step back, or pick a date from a calendar):
  - a minute-by-minute timeline of the app in front, with CPU and GPU temperature drawn over it;
  - time per app, split into *in use*, *in the background* and *minimized*;
  - peak CPU and GPU temperature, hot spot, voltage and power, with the time and the app in front;
  - your longest sessions (a minute or more; quick switches still count towards app time);
  - highlights, shown only when there's something to report: time against your daily average, the longest stretch without a break, temperatures compared at the same load (idle against idle, heavy load against heavy load), minutes at your alert limit, and a GPU hot spot running far above the core (a sign the thermal paste needs redoing).
- **Active time, not just time open**: time counts as active only while you're using the app; a fullscreen game counts even when you're not touching the mouse. Time away from the PC is counted separately.

### 🧩 Apps
- Per app, for any single day, the last 7 or 30 days, or all time: active, background and minimized time, average and peak CPU/GPU temperature while it was in front, peak memory, average CPU use, sessions and a 14-day chart.
- Rename apps, change their category (game, browser, work…), or exclude them from tracking.

### 💥 Crashes
- App and game crashes, freezes, graphics driver resets, blue screens and sudden shutdowns, all read from Windows' own records.
- **Plain-language explanations**: blue screen codes and faulting modules are translated into what probably happened and what to try.
- Shows what was going on just before each crash: CPU and GPU temperatures, and which game you were in and for how long.
- **List** (every crash, newest at the top) or **Grouped**: the same crash repeated is one row with a count ("Wallpaper Engine crashed ×36"), and several things failing within minutes is one incident ("Your PC froze: 5 apps stopped responding").
- **A timeline** of problems per day, coloured by severity, with the days a driver or Windows update was installed marked, and **"what changed before"**: a blue screen that started two days after a graphics driver install says so.
- **Copy report** (a ready-to-paste summary with your CPU, GPU and driver, RAM and Windows version), **Search online**, and **Show dump file** for blue screens.
- **Mute** an app you don't care about: its crashes leave the totals, timeline, reports and notifications (nothing is deleted).
- Any single day (pick it from a calendar, or click a day on the timeline), the last 7, 30 or 90 days, or everything (including what Windows logged before Rigsight was installed).
- **Patterns**, such as "3 of 4 shutdowns happened while asleep" or "the graphics driver was involved in 5 crashes".
- Filter by **Apps & games** or **PC problems**.

### 🧱 Dashboards
- **Build your own dashboards** from tiles: gauges, a temperature chart, any single sensor, fans, drives, top memory users, today's totals, most used apps, highlights, yesterday and crashes.
- **Drag tiles anywhere, and resize them by dragging their edges.** The others slide out of the way and fill the gaps.
- **Make as many as you like**, such as "Gaming" or "Work", and pick one to open Rigsight on. Each is saved automatically.

### 🎮 Game overlay
- **Press `Alt+Shift+O` in any game** to show or hide a compact readout: FPS, frame time and 1% lows, CPU and GPU temperature, load, clock and power, hot spot, video memory, RAM, the game you're playing and for how long, and the time.
- **Pick exactly what it shows**, which corner it sits in, one row per part or a single line, its size and opacity. Change the shortcut to anything you like.
- **Add any of your sensors** (up to 10): a case fan, a pump, a voltage, a drive or motherboard temperature, anything on All sensors, each with a short name if you like. They stay live in games, even with Rigsight's window closed.
- **Never gets in the way**: it never takes focus, and clicks pass straight through it.
- **Works in every game, exclusive fullscreen included**, through [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) (free, the engine behind MSI Afterburner's overlay). Rigsight hands it the readings and RivaTuner draws them inside the game. The installer offers to install RivaTuner, and Rigsight keeps it running. Without it, the overlay still shows over borderless and windowed games.
- **Safe with anti-cheat**: Rigsight itself never hooks into games. Its own overlay is a normal always-on-top window, and RivaTuner is a long-established tool that anti-cheat systems accept.

### 🖥️ On your desktop
- **Six widgets**: Compact, Slim bar, Gauges, Now playing, Today, Temperature graph. Each has a dark, light or system theme, sizes, opacity and a click-through lock.
- **Tray icon** with a health dot (green, amber or red) and a live readout on hover.
- **Calm notifications**: a daily recap, game session summaries, temperature alerts that ignore brief spikes, and (optionally) a plain-language note when something crashes. Non-urgent cards wait until you leave a fullscreen game.

### 🎛️ Your control
- Dozens of settings: what gets tracked, how long history is kept, alert thresholds, units, a black or white app theme (or follow Windows), widget looks, and start with Windows.
- Pause tracking at any time from the tray.
- Clear your history with one click.

## Install

1. Download **`Rigsight-Setup-x.y.z.exe`** from the [Releases](../../releases) page.
2. Run it. Windows may show *"Windows protected your PC"* because the installer isn't code-signed. Click **More info → Run anyway**.
3. Accept the one admin prompt. The installer sets up the background agent to start with Windows (you can turn this off in Settings).
4. Leave **Install the PawnIO driver** and **Install RivaTuner Statistics Server** ticked if they're offered. PawnIO is needed for CPU and motherboard sensors, RivaTuner for the overlay in exclusive-fullscreen games. Both can take a minute or more to download.

To uninstall, use **Settings → Apps → Rigsight → Uninstall**. This removes the program and its startup task. Your history stays in `%LocalAppData%\Rigsight`; delete that folder too for a clean removal.

## Privacy

Everything stays on your PC, in `%LocalAppData%\Rigsight`. Rigsight has **no telemetry, no account and no network access**.

It records which app is in front and how hard your hardware is working. It **never** records window titles, keystrokes, screenshots or file contents.

## How it stays light

Rigsight is split into two programs:

| | Runs | Does |
|---|---|---|
| **`Rigsight.Agent.exe`** | Always, from sign-in, with admin rights | Reads sensors, notices which app is in front, writes one small summary per minute to a local SQLite database, and draws the tray icon, widgets, game overlay and notifications. |
| **`Rigsight.exe`** | Only while you have the window open | The app window. It needs no admin rights, reads history from the database, streams live data from the agent over a named pipe, and **fully exits when closed**. |

### Measured resource use

Measured over 60 seconds on a Ryzen 7 5700X3D (16 threads) with an RTX 3080 Ti and 202 sensors. CPU is the share of the whole PC; memory is private memory, the "Memory" column in Task Manager.

| | CPU | Memory |
|---|---|---|
| **Background agent, window closed** (the normal all-day state) | **~0.006%** | **~50 MB** |
| Background agent with the game overlay showing | ~0.02% | ~56 MB |
| Background agent while the window is open (reads every sensor each second) | ~0.13–0.18% | ~60–75 MB |
| Rigsight window, open on a page | ~0.07–0.09% | ~140–160 MB (pages you've opened stay loaded, so switching back is instant) |

Close the window and its memory is released completely; the agent drops back to the first row.

The agent is built to be almost invisible:

- **Tiered sensors**: fast sensors like temperatures and load are read every second, slow ones like drive health every few minutes, and static ones once. While the window is closed, only what the widgets and tray need is read.
- **An NVIDIA fast path**: GPU temperature and load come from NVIDIA's lightweight NVML/NVAPI calls instead of a full driver query, which is about 70× cheaper.
- **One system call for every process**: per-app CPU and memory come from a single `NtQuerySystemInformation` call per sample, not thousands of per-process queries.
- **No UI framework in the background**: widgets, the overlay, tray and notifications are drawn with plain GDI+ into layered windows.
- **Frugal by design**: below-normal priority, a conserving garbage collector, and nothing kept in memory that the database already has.

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
- is about 50 MB and self-contained, so the target PC doesn't need .NET;
- installs to Program Files and adds Start menu and (optional) desktop shortcuts;
- offers to install PawnIO and RivaTuner Statistics Server;
- stops a running copy before updating;
- removes the startup task when uninstalled.

The version number comes from `<Version>` in `Directory.Build.props`.

To publish a release:
```powershell
gh release create vX.Y.Z dist\Rigsight-Setup-X.Y.Z.exe --title "Rigsight X.Y.Z" --notes-file notes.md
```

## Project layout

```
Rigsight.slnx
Directory.Build.props     version + shared build settings
src/
  Rigsight.Core/          shared by both programs
    Settings/               settings model + JSON store (the agent is the only writer)
    Protocol/               messages sent between agent and app
    Data/                   SQLite schema, writer and queries
    Reports/                report builder + insight engine
    Stability/              crash log reader + plain-language explainer
    Apps/                   app naming and categories (games, browsers, ...)
  Rigsight.Agent/         background agent (WinForms, no main window)
    Sensors/                LibreHardwareMonitor host, NVIDIA fast path, sensor history
    Tracking/               foreground/idle detection, per-process sampler, sessions
    Widgets/                GDI+ widget renderer and layered windows
    Ui/                     tray icon, menus, notification cards
    Ipc/                    named-pipe server
  Rigsight/               the app window (WPF, Fluent, dark and light themes, MVVM)
    Views/ ViewModels/ Controls/ Services/ Themes/
installer/Rigsight.iss    Inno Setup script
tools/                    build-installer.ps1, make-icon.ps1
assets/                   logo and icon
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
In `%LocalAppData%\Rigsight`: `settings.json`, the `rigsight.db` SQLite database, and a small log.

## Credits

- Hardware readings: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and the [PawnIO](https://pawnio.eu) driver
- MVVM: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- Storage: [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore)
- Installer: [Inno Setup](https://jrsoftware.org/isinfo.php)
- In-game overlay in fullscreen games: [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) by Unwinder (optional, installed separately)

## License

[MIT](LICENSE)
