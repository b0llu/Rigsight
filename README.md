<p align="center">
  <img src="assets/Rigsight.png" width="96" alt="Rigsight logo" />
</p>

<h1 align="center">Rigsight</h1>

<p align="center">
  <b>Know your rig.</b><br />
  An intelligent PC tracker for Windows. Temperatures, app usage, crashes and storage, explained in plain language.
</p>

<p align="center">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" />
  <img alt="CPU cost" src="https://img.shields.io/badge/background%20CPU-~0.02%25-2ea043" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-blue" />
</p>

---

Most hardware monitors show you a wall of numbers *right now*. Rigsight remembers them and tells you what they mean.

Play games all day, come back in the evening, and Rigsight can tell you:

- what you played and for how long;
- which game pushed your GPU the hottest;
- what sat open in the background doing nothing;
- why your PC restarted on its own last Tuesday.

It does all of this while using about **0.02% of your CPU**.

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

### 📅 Your day
- **Home**: today so far, highlights, and a recap of yesterday.
- **Reports** for the day, week and month:
  - a timeline of your day with temperatures drawn over it;
  - time per app, split into *in use*, *in the background* and *minimized*;
  - the hottest moments and the app responsible;
  - your longest sessions, plus insights like "Cyberpunk ran your GPU 9 °C hotter than anything else".
- **Discord-style sessions**: Rigsight knows when you're actually using an app and when you're away, and counts a fullscreen game as in use even when you're not touching the mouse.

### 🧩 Apps
- Every app you use, with active time, average and peak CPU/GPU temperatures while it was in front, peak memory, sessions and a 14-day chart.
- Rename apps, change their category (game, browser, work…), or exclude them from tracking.

### 💥 Crashes
- App and game crashes, freezes, graphics driver resets, blue screens and sudden shutdowns, all read from Windows' own records.
- **Plain-language explanations**: blue screen codes and faulting modules are translated into what probably happened and what to try.
- Shows how hot your CPU and GPU were just before each crash.
- **Patterns**, such as "3 of 4 shutdowns happened while asleep" or "the graphics driver was involved in 5 crashes".
- Filter by **Apps & games** or **PC problems**.

### 🌡️ Hardware
- **Temperatures**: live CPU and GPU gauges, hot spot and memory junction, per-core load, fans, motherboard and drive temperatures, and history.
- **Memory**: RAM use, live per-app memory (like Task Manager's "private working set"), and today's biggest memory users.
- **Storage**: drive usage and growth, drive health, cleanup suggestions, and a folder scanner with a treemap.
- **All sensors**: every sensor LibreHardwareMonitor can see, searchable, in collapsible groups, with rename and hide.

### 🧱 Dashboards
- **Build your own dashboards** from tiles: gauges, a temperature chart, any single sensor, fans, drives, top memory users, today's totals, most used apps, highlights, yesterday and crashes.
- **Drag tiles anywhere, and resize them by dragging their edges.** The others slide out of the way and fill the gaps.
- **Make as many as you like**, such as "Gaming" or "Work", and pick one to open Rigsight on. Each is saved automatically.

### 🖥️ On your desktop
- **Six widgets**: Compact, Slim bar, Gauges, Now playing, Today, Temperature graph. Each has themes, sizes, opacity, click-through lock, and a *game overlay* mode that only appears over fullscreen games.
- **Tray icon** with a health dot (green, amber or red) and a live readout on hover.
- **Calm notifications**: a daily recap, game session summaries, and temperature alerts that ignore brief spikes. Non-urgent cards wait until you leave a fullscreen game.

### 🎛️ Your control
- Dozens of settings: what gets tracked, how long history is kept, alert thresholds, units, widget looks, and start with Windows.
- Pause tracking at any time from the tray.
- Clear your history with one click.

## Install

1. Download **`Rigsight-Setup-x.y.z.exe`** from the [Releases](../../releases) page.
2. Run it. Windows may show *"Windows protected your PC"* because the installer isn't code-signed. Click **More info → Run anyway**.
3. Leave **Install the PawnIO driver** ticked if it's offered. CPU and motherboard temperatures need it.
4. Open Rigsight. The background agent asks for admin rights **once**, then starts with Windows silently (you can turn this off in Settings).

To uninstall, use **Settings → Apps → Rigsight → Uninstall**. This removes the program and its startup task. Your history stays in `%LocalAppData%\Rigsight`; delete that folder too for a clean removal.

## Privacy

Everything stays on your PC, in `%LocalAppData%\Rigsight`. Rigsight has **no telemetry, no account and no network access**.

It records which app is in front and how hard your hardware is working. It **never** records window titles, keystrokes, screenshots or file contents.

## How it stays light

Rigsight is split into two programs:

| | Runs | Does |
|---|---|---|
| **`Rigsight.Agent.exe`** | Always, from sign-in, with admin rights | Reads sensors, notices which app is in front, writes one small summary per minute to a local SQLite database, and draws the tray icon, widgets and notifications. |
| **`Rigsight.exe`** | Only while you have the window open | The app window. It needs no admin rights, reads history from the database, streams live data from the agent over a named pipe, and **fully exits when closed**. |

### Measured resource use

Measured over 60 seconds on a Ryzen 7 5700X3D (16 threads) with an RTX 3080 Ti and 202 sensors. CPU is the share of the whole PC; memory is private memory, the "Memory" column in Task Manager.

| | CPU | Memory |
|---|---|---|
| **Background agent, window closed** (the normal all-day state) | **0.016%** | **~79 MB** |
| Background agent while the window is open (reads every sensor each second) | ~0.13% | ~80 MB |
| Rigsight window, open on a page | ~0.03–0.07% | ~115–140 MB |

Close the window and its memory is released completely; the agent drops back to the first row.

The agent is built to be almost invisible:

- **Tiered sensors**: fast sensors like temperatures and load are read every second, slow ones like drive health every few minutes, and static ones once. While the window is closed, only what the widgets and tray need is read.
- **An NVIDIA fast path**: GPU temperature and load come from NVIDIA's lightweight NVML/NVAPI calls instead of a full driver query, which is about 70× cheaper.
- **One system call for every process**: per-app CPU and memory come from a single `NtQuerySystemInformation` call per sample, not thousands of per-process queries.
- **No UI framework in the background**: widgets, tray and notifications are drawn with plain GDI+ into layered windows.
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
- offers to install PawnIO;
- stops a running copy before updating;
- removes the startup task when uninstalled.

The version number comes from `<Version>` in `Directory.Build.props`.

To publish a release:
```powershell
gh release create v0.2.0 dist\Rigsight-Setup-0.2.0.exe --title "Rigsight 0.2.0" --notes "First public build"
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
  Rigsight/               the app window (WPF, Fluent dark theme, MVVM)
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
No. The agent uses about 0.02% of total CPU and runs at below-normal priority. Non-urgent notifications also wait until you leave fullscreen.

**Does it work with AMD (or Intel) graphics cards and CPUs?**
Yes. All hardware is read through LibreHardwareMonitor, which supports NVIDIA, AMD and Intel GPUs and Intel and AMD CPUs. NVIDIA cards also get an extra fast path (NVML/NVAPI) because NVIDIA's full driver query is unusually expensive. Other cards use the standard route, which is already light.

**Where is my data?**
In `%LocalAppData%\Rigsight`: `settings.json`, the `rigsight.db` SQLite database, and a small log.

## Credits

- Hardware readings: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and the [PawnIO](https://pawnio.eu) driver
- MVVM: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- Storage: [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore)
- Installer: [Inno Setup](https://jrsoftware.org/isinfo.php)

## License

[MIT](LICENSE)
