<p align="center">
  <img src="assets/Rigsight.png" width="96" alt="Rigsight logo" />
</p>

<h1 align="center">Rigsight</h1>

<p align="center">
  <b>Know your rig.</b><br />
  Your PC's temperatures, usage and crashes, recorded all day and kept as history.
</p>

<p align="center">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white" />
  <img alt="CPU cost" src="https://img.shields.io/badge/background%20CPU-~0.01%25-2ea043" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-blue" />
</p>

---

Most hardware monitors only show numbers while you're watching. Rigsight keeps them, so you can look back at any day, week, month or year: how hot your GPU got in each game, which apps took your time, and why your PC crashed last Tuesday.

It records quietly in the background using about **0.01% of your CPU**, and everything stays on your PC.

## What it does

- **🌡️ Every degree, with a timestamp.** Live CPU and GPU temperatures, fans, drives and every other sensor, with each day's highs and a history you can scroll back through.
- **📅 Your day, minute by minute.** Reports for any day, week, month or year: which app was in front, how hot things ran, your longest sessions, and anything worth noticing.
- **🧩 Where your time and heat go.** Time per app, how hot each game runs your PC, and how that changes over time.
- **💥 Crashes, explained in plain words.** App crashes, freezes, driver resets, blue screens and sudden shutdowns, with what probably happened, what to try, and what changed before it started.
- **🎮 Your numbers, inside the game.** Press `Alt+Shift+O` for a small overlay with FPS, temperatures, load and anything else you pick, in colour or grayscale. It works in fullscreen games too and is safe with anti-cheat.
- **🧱 Your own dashboards.** Build pages from tiles and arrange them however you like.
- **🖥️ On your desktop.** Six widgets, a tray icon with a health dot, and calm notifications: a daily recap, game summaries and temperature alerts.
- **💾 Storage and memory.** Drive space and health, cleanup suggestions, and which apps use your memory.
- **⚫ Black or white.** A pure black or pure white app, or let it follow Windows.

## Install

**One command.** Open PowerShell (press Start, type *PowerShell*) and paste:

```powershell
irm https://b0llu.github.io/Rigsight/install.ps1 | iex
```

**Or download it** from the [Releases](../../releases) page and run `Rigsight-Setup-x.y.z.exe`. Windows may say *"Windows protected your PC"* because the installer isn't code-signed: click **More info → Run anyway**.

Leave **PawnIO** and **RivaTuner** ticked if the installer offers them. PawnIO reads CPU and motherboard sensors, and RivaTuner shows the overlay in fullscreen games.

**Updates** arrive by themselves: a new version downloads in the background and installs the next time you start your PC, or straight away when you click *Restart*. You can turn this off in Settings, and Rigsight will just tell you when there's a new version.

**To uninstall**, go to **Settings → Apps → Rigsight → Uninstall**. Your history stays in `%LocalAppData%\Rigsight`; delete that folder too if you want it gone.

## Privacy

No account, no telemetry. The only thing Rigsight checks online is whether there's a new version. Everything else lives in one folder on your PC, and you can clear it with one click.

It knows which app is in front and how hard your hardware is working. It never records window titles, what you type, or what's on your screen.

## FAQ

**Why does it need admin rights?**
Windows only lets programs with admin rights read CPU and motherboard sensors. Only the small background part runs as admin, and you're asked once, not at every start.

**My CPU temperature is missing.**
Install the PawnIO driver (`winget install namazso.PawnIO`) and restart Rigsight.

**Does it slow down games?**
No. It uses about 0.01% of your CPU and waits until you leave a fullscreen game to show anything that isn't urgent.

**The overlay doesn't show in my game.**
Open the Overlay page: it tells you what's missing and can install RivaTuner for you. A game that was already open when RivaTuner started needs a restart.

**Does it work with AMD and Intel?**
Yes: NVIDIA, AMD and Intel graphics cards, and Intel and AMD processors.

## Building from source

You need Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`).

```powershell
dotnet build Rigsight.slnx
.\bin\Debug\Rigsight.Agent.exe   # the background part (asks for admin once)
.\bin\Debug\Rigsight.exe         # the app window
```

To make the installer, install [Inno Setup](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and run `tools\build-installer.ps1`. The version comes from `Directory.Build.props`.

## Credits

- Hardware readings: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and the [PawnIO](https://pawnio.eu) driver
- Overlay in fullscreen games: [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) by Unwinder
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore), [Inno Setup](https://jrsoftware.org/isinfo.php)

## License

[MIT](LICENSE)
